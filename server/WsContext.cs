using System;
using System.Threading;
using System.Net.Sockets;
using System.Threading.Tasks;

#pragma warning disable CA1835  // CA1835: ReadAsync(), WriteAsync() を、ReadOnlyMemory<> 呼び出しに変更する

/////////////////////////////////////////////////////////////////////////////////////////

partial class WsContext
{
	const int EN_msec_WS_WriteTimeout = 5000;  // WS の Write に関しては、５秒をタイムアウトの設定しにしている

	static MemBlk_Pool ms_mem_blk_pool_for_WS_Buf = null;
	public static void Set_mem_blk_pool_for_WS_Buf(MemBlk_Pool mem_blk_pool) => ms_mem_blk_pool_for_WS_Buf = mem_blk_pool;

	// ---------------------------------------------
	Ws_RecvBuf m_ws_Recv_buf = null;
	Ws_Sender m_ws_Sender = null;

	readonly ushort m_idx_ws_context;

	// ws データ受信用と、ハートビート受信用の複数箇所で利用するために、メンバ変数としている
	NetworkStream m_ns_ws = null;

	// ws コマンド進行のためのセマフォ
	SemaphoreSlim m_semph_WS_Cmd;
	public void Up_semph_WS_Cmd() => m_semph_WS_Cmd.Release();
	bool mb_abort_ws_context = false;

	// 以下は、「unused」でなく「unget」
	uint m_cs_pos_unget = 0;
	ushort m_cs_idx_unget = 0;

	CancellationTokenSource m_cts_for_ReadAsync_ws_ns = null;

	// =================================================
	// 以下は暫定的に存在するメンバ変数
	bool mb_is_code_displayed_on_client = false;  // CMD_Req_Doc が可能かどうかに利用される
	public bool bIs_Code_Displayed_on_client() => mb_is_code_displayed_on_client;

	// ------------------------------------------------------------------------------------
	enum WS_ID
	{
//		EN_Pong,
		EN_WS_Close,
		EN_none,  // 通常処理を終えて、特に通知することがない場合
		EN_Invalid,  // 不正な ws パケットを送ってきたため、相手を切断する
	}

	// ------------------------------------------------------------------------------------
	public WsContext(ushort idx_ws_context)
	{
		m_idx_ws_context = idx_ws_context;
	}

	// ------------------------------------------------------------------------------------
	// Abort_WS_Context() は、WS_Spawn_Context() の最初の await 以降にコールされることを想定している
	// この関数がコールされるということは、m_cts_for_ReadAsync_ws_ns と m_semph_WS_Cmd は生成済みとなっている

	public void Abort_WS_Context()
	{
		if (m_cts_for_ReadAsync_ws_ns.IsCancellationRequested == false) { m_cts_for_ReadAsync_ws_ns.Cancel(); }

		mb_abort_ws_context = true;
		m_semph_WS_Cmd.Release();
	}

	// ------------------------------------------------------------------------------------
	public async Task WS_Spawn_Context(NetworkStream ns_WS, string str_ws_accept_key)
	{
//		ms_iLog.WrtLine($"--- html : {m_idx_ws_context} === Upgrade to WebSocket ---");
		m_ns_ws = ns_WS;
		m_ns_ws.WriteTimeout = EN_msec_WS_WriteTimeout;

		Task task_cmd_thread = null;
		bool bDo_Add_WsContext = false;

		using (MemBlk mem_blk_WS_Recv_Buf = ms_mem_blk_pool_for_WS_Buf.Lease_MemBlk())
		using (MemBlk mem_blk_WS_Send_Stream_Buf = ms_mem_blk_pool_for_WS_Buf.Lease_MemBlk())
		using (m_cts_for_ReadAsync_ws_ns = new CancellationTokenSource())
		using (m_semph_WS_Cmd = new SemaphoreSlim(0))  // m_semph_WS_Cmd は停止状態からスタートする
		try
		{
			byte[] ary_buf_Recv = mem_blk_WS_Recv_Buf.Get_ary_buf();
			m_ws_Sender = new Ws_Sender(m_ns_ws, mem_blk_WS_Send_Stream_Buf.Get_ary_buf());
			m_ws_Recv_buf = new Ws_RecvBuf(m_ns_ws, ary_buf_Recv, m_idx_ws_context, m_cts_for_ReadAsync_ws_ns);

			// --------------------------------------------------------
			// WebSocket のオープン処理
			await m_ws_Sender.Send_WS_Accept_Response(str_ws_accept_key);

			// WebSocket をオープンしたため、コマンドスレッドを起動し、コマンドストリームに登録する
			// m_pos_unget, m_cs_idx_unget には、「CMD_Req_Doc」or「CMD_No_Doc」を書き込んだ位置の情報が返される
			CmdStream.Add_WsContext(this, out m_cs_pos_unget, out m_cs_idx_unget);
			bDo_Add_WsContext = true;

			// m_pos_unget と m_cs_idx_unget の値をもらった後に、WS_Cmd_Thread を起動すること
			task_cmd_thread = Task.Run(this.WS_Cmd_Thread);

			// --------------------------------------------------------
			// WebSocket による通信開始
			for (;;)
			{
				if (Server.Is_in_ShuttingDown() || mb_abort_ws_context) { break; }  // for (;;)

				int bytes_recv = await m_ns_ws.ReadAsync(ary_buf_Recv, 0, Common.EN_bytes_WS_Buf
																	, m_cts_for_ReadAsync_ws_ns.Token);
//				ms_iLog.WrtLine($"--- ws : {m_idx_ws_context} -- 受信 {bytes_recv} bytes");

				if (bytes_recv == 0) { break; }  // 相手側がクローズした場合
				if (Server.Is_in_ShuttingDown() || mb_abort_ws_context) { break; }  // for (;;)

				// リードバッファの pos 等の初期化
				m_ws_Recv_buf.Set_bytes_recv(bytes_recv);

				for (;;)
				{
					// WSパケットを「１つ」処理する
					if (await m_ws_Recv_buf.Read_Next() == false) { goto TERMINATE_WS_ReadAsync; }
					if (m_ws_Recv_buf.Is_Complete_toRead() == true) { break; }
				}
			}

TERMINATE_WS_ReadAsync:;
		}
		catch (OperationCanceledException) {}  // m_ns_ws.ReadAsync() をキャンセルした場合（特に何もすることがない）
		catch (System.IO.IOException ex)
		{
			if (ex.InnerException is SocketException ex_socket)
			{
				if (ex_socket.ErrorCode == 10053 || ex_socket.ErrorCode == 10054)
				{
					// 10053 -> 「確立された接続がホスト コンピューターのソフトウェアによって中止されました。」
					// 10054 -> 「既存の接続はリモートホストに強制的に切断されました。」
					// この場合、特に何もすることがない。
					goto FINISH_IOException;
				}
			}

			ms_iLog.Wrt_Warning_Line($"!!! ws : {m_idx_ws_context} -> System.IO.IOException 補足 : {ex}\r\n");
FINISH_IOException:;
		}
      catch (Exception ex)
      {
			if (Server.Is_in_ShuttingDown() == true)
			{
				// Abort_TcpContext() で、アボートされた可能性が高い
//				KLog.Wrt_BkBlue($"+++ 例外補足 Spawn_Context() : 恐らくサーバーシャットダウンのため。/ idx_clt_context -> {m_str_idx_clt_context}\r\n");
			}
			else
			{
				ms_iLog.Wrt_Warning_Line($"!!! ws : {m_idx_ws_context} -> 例外補足 : {ex}\r\n");
			}
      }
		finally
		{
			// ++++++++++++++++++++++++++++++++++++++++++++++++++++++++++
			// TODO: 送信途中のリソースがあれば、ここで解放すること
			// Server.Is_under_send_operation() で送信途中かどうか判定できる
			// ++++++++++++++++++++++++++++++++++++++++++++++++++++++++++

			// mem_blk_WS_Send_Buf を Dispose する前に、mem_blk_WS_Send_Buf を利用する WS_Cmd_Thread が停止する必要がある。
			mb_abort_ws_context = true;
			if (task_cmd_thread != null)
			{
				if (task_cmd_thread.IsCompleted != false)
				{
					m_semph_WS_Cmd.Release();
					await task_cmd_thread;
				}
			}

			if (bDo_Add_WsContext == true)
			{
				if (CmdStream.Remove_WsContext(this) == false)
				{
					// エラー顕在化
					ms_iLog.Wrt_Warning_Line($"!!! ws : {m_idx_ws_context} -> CmdStream.Remove_WsContext() に失敗しました。");
				}
			}
		} // using, try

//		ms_iLog.WrtLine($"--- ws : {m_idx_ws_context} === context を終了しました。---");
	}

	// ------------------------------------------------------------------------------------
	void WS_Cmd_Thread()
	{
		CS_Header cs_header = new ();

		// 以下の変数は、Req_Doc を利用する暫定的な方法に対応するため（完成版では不要となる）
		Span<byte> span_ping_send_buf = stackalloc byte[4];
		span_ping_send_buf[0] = 0x89;  // WS Ping
		span_ping_send_buf[1] = 0x02;  // Payload len = 2

		try
		{
			// --------------------------------------------------------
			// display 表示が可能になるまでの処理が実行される

			// m_pos_unget と m_cs_idx_unget には、「CMD_Req_Doc」or「CMD_No_Doc」が書き込まれた位置の情報が設定されている。
			CmdStream.GetNextCmd(ref m_cs_pos_unget, ref m_cs_idx_unget, ref cs_header);

			if (cs_header.m_cmdID == CS_ID.CS_No_Doc)
			{
				// CS_ID.CMD_No_Doc の通知は行わないことにした
//				m_ws_Sender.Alloc_Buf(cs_header.m_cs_idx, CS_ID.CMD_No_Doc, 0);
//				await m_ws_Sender.Send_by_WS();
			}
			else if (cs_header.m_cmdID == CS_ID.CS_Qry_Req_Doc)
			{
				// 現在の暫定的な挙動では、以下のようなことをしても意味が薄いと思うため、単純な挙動をさせることにした

				// m_pos_unget, m_cs_idx_unget の情報を、「CMD_Req_Doc 直後」の状態に保存しておく。
				// わずかなタイミングのずれで、「CMD_UpAllText」の直前の変更事項を逃す可能性があるため。
//				uint tmp_pos_unget = m_pos_unget;
//				ushort tmp_cs_idx_unget = m_cs_idx_unget;

				for (;;)
				{
					m_semph_WS_Cmd.Wait();
					if (mb_abort_ws_context == true) { goto TERMINATE_WS_Cmd_Thread; }

					CmdStream.GetNextCmd(ref m_cs_pos_unget, ref m_cs_idx_unget, ref cs_header);

					if (cs_header.m_cmdID == CS_ID.CMD_UpAllText) { break; }
					if (cs_header.m_cmdID == CS_ID.CS_ERROR)
					{
						ms_iLog.Wrt_Warning_Line("!!! WsContext.WS_Cmd_Thread() : CMD_UpAllText 待機中に CMD_ERROR を検出しました。");
						goto TERMINATE_WS_Cmd_Thread;
					}
				}

				// cs_header.m_cmdID == CS_ID.CMD_UpAllText のとき、ここにくる
				// CMD_UpAllText で Up されたものを、そのまま丸ごと Down することにした（cs_idx も含めて）
				int allocd_pos_in_send_buf = m_ws_Sender.Alloc_Raw_Buf((int)cs_header.m_bytes_payload);

				// CMD_UpAllText の内容を全て WS 送信バッファにコピーする
				CmdStream.NoLc_Copy_CS_buf_to(m_ws_Sender.ma_sendr_buf, allocd_pos_in_send_buf
															, cs_header.m_cs_pos_payload, cs_header.m_bytes_payload);
				m_ws_Sender.Send_by_WS();
			}
			else
			{
				// エラー顕在化
				ms_iLog.Wrt_Warning_Line("!!! WsContext.WS_Cmd_Thread() : 最初の１コマンドが「CMD_No_Doc」or「CMD_Req_Doc」ではありませんでした。");
				goto TERMINATE_WS_Cmd_Thread;
			}

			// --------------------------------------------------------
			// CMD_No_Doc or CMD_UpAllText の処理は終えたため、CMD_none となるまで情報をクライアントに送って、入力可能な状態にもっていく
			{
				bool b_need_to_send = false;
				for (;;)
				{
					CmdStream.GetNextCmd(ref m_cs_pos_unget, ref m_cs_idx_unget, ref cs_header);
				
					if (cs_header.m_cmdID == CS_ID.CS_none) { break; }

					if (cs_header.m_cmdID == CS_ID.CMD_INSERT)
					{
						m_ws_Sender.Copy_frm_CS_Payload(cs_header.m_cs_pos_payload, cs_header.m_bytes_payload);
						b_need_to_send = true;
						break;
					}

					if (cs_header.m_cmdID == CS_ID.CMD_REMOVE)
					{
						m_ws_Sender.Copy_frm_CS_Payload(cs_header.m_cs_pos_payload, cs_header.m_bytes_payload);
						b_need_to_send = true;
						break;
					}
					
					// ###################################
					// ここに追加で対応しなくてはならない CMD の処理をすること




				


					if (cs_header.m_cmdID == CS_ID.CS_ERROR)
					{
						ms_iLog.Wrt_Warning_Line("!!! WsContext.WS_Cmd_Thread() : CMD_display_OK 待機中に CMD_ERROR を検出しました。");
						goto TERMINATE_WS_Cmd_Thread;
					}
				}
				if (b_need_to_send == true) { m_ws_Sender.Send_by_WS(); }
			}

			// CMD_Display_OK を送信して、クライアントが入力可能な状態にする
			m_ws_Sender.Alloc_Buf_withID(CS_ID.CMD_Display_OK, 2, (byte)CmdStream.Get_WsContexts_Count());
			m_ws_Sender.Send_by_WS();
			mb_is_code_displayed_on_client = true;

			// --------------------------------------------------------
			// クライアントが最初の画面表示を実行できたため、以降は通常処理に移行する（m_semph_WS_Cmd 駆動によって処理が進む）
			// 以下の for (;;) ループから抜け出すのは
			// (1) m_semph_WS_Cmd 駆動 -> m_ws_Sender.Send_by_WS() 等により m_ns_ws.Write() が実行され、そこで例外が発生する
			// (2) mb_abort_ws_context == true による break

			for (;;)
			{
				m_semph_WS_Cmd.Wait();
				if (mb_abort_ws_context == true) { break; }

				bool b_need_to_send = false;
				for (;;)
				{
					CmdStream.GetNextCmd(ref m_cs_pos_unget, ref m_cs_idx_unget, ref cs_header);

					switch (cs_header.m_cmdID)
					{
					case CS_ID.CS_none:
						goto BREAK_CmdStream処理;

					case CS_ID.CMD_Chg_NumUsrs:
						m_ws_Sender.Alloc_Buf_withID(CS_ID.CMD_Chg_NumUsrs, 2, (byte)CmdStream.Get_WsContexts_Count());
						b_need_to_send = true;
						break;

					case CS_ID.CS_Qry_Req_Doc:  // Ping を CS_Qry_Req_Doc の代わりに流用する
						lock (m_ns_ws)
						{
							span_ping_send_buf[2] = (byte)(cs_header.m_cs_idx & 0xff);
							span_ping_send_buf[3] = (byte)(cs_header.m_cs_idx >> 8);

							m_ns_ws.Write(span_ping_send_buf);  // Ping の発行
						}
						break;

					case CS_ID.CMD_INSERT:
						// 自分が発行した INSERT であれば、処理する必要はない
						if (cs_header.m_idx_ws_context_issuer == m_idx_ws_context) { break; }

						m_ws_Sender.Copy_frm_CS_Payload(cs_header.m_cs_pos_payload, cs_header.m_bytes_payload);
						b_need_to_send = true;
						break;

					case CS_ID.CMD_REMOVE:
						// 自分が発行した REMOVE であれば、処理する必要はない
						if (cs_header.m_idx_ws_context_issuer == m_idx_ws_context) { break; }

						m_ws_Sender.Copy_frm_CS_Payload(cs_header.m_cs_pos_payload, cs_header.m_bytes_payload);
						b_need_to_send = true;
						break;

					// ###################################
					// ここに追加で対応しなくてはならない CMD の処理をすること





					}
				}
BREAK_CmdStream処理:
				if (b_need_to_send == true) { m_ws_Sender.Send_by_WS(); }
			}

TERMINATE_WS_Cmd_Thread:;
		}
		// ここでは、特に何もすることがない。例外を外に出さないようにするのみ
		catch (Exception ex)
		{
			ms_iLog.Wrt_Warning_Line($"!!! WsContext.WS_Cmd_Thread() : 例外検出\r\n{ex}");
		}

		mb_abort_ws_context = true;
		if (m_cts_for_ReadAsync_ws_ns.IsCancellationRequested == false) { m_cts_for_ReadAsync_ws_ns.Cancel(); }
	}

	// ==================================================
	// ログ用
	static ILog ms_iLog = null;

	[System.Runtime.CompilerServices.ModuleInitializer]
	public static void Module_Init() => ms_iLog = Program.Get_iLog();
}

/////////////////////////////////////////////////////////////////////////////////////////
