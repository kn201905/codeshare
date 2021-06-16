using System;
using System.Threading;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Text;
using WS_ID = WS_Recv_Buf.WS_ID;

#pragma warning disable CA1835  // CA1835: ReadAsync(), WriteAsync() を、ReadOnlyMemory<> 呼び出しに変更する

/////////////////////////////////////////////////////////////////////////////////////////

class WsContext
{
	static MemBlk_Pool ms_mem_blk_pool_WS = null;
	public static void Set_mem_blk_pool(MemBlk_Pool mem_blk_pool) => ms_mem_blk_pool_WS = mem_blk_pool;

	// ハートビート送信用バッファ（ハートビート受信バッファは、通常受信バッファが利用される）
	static readonly byte[] ms_heart_beat_send_buf = new byte[2] { 0x89, 0x00 };

	// ---------------------------------------------
	WS_Recv_Buf m_ws_Recv_buf;
	WS_Send_Buf m_ws_Send_buf;

	readonly uint m_idx_ws_context;		// コンストラクタで設定

	// ws データ受信用と、ハートビート受信用の複数箇所で利用するために、メンバ変数としている
	NetworkStream m_ns_ws = null;

	// // ハートビート書きこみのためのセマフォ（m_ns_ws の読み込み待機は、１ヶ所だけでしかできない）
	SemaphoreSlim m_sem_for_ns_ws_write;

	bool mb_receive_heart_beat_on_ws_spawn_context_thread = false;
	bool mb_failed_heart_beat = false;

	CancellationTokenSource m_cts_for_ReadAsync_ws_ns = null;
	// ハートビート interval wait をキャンセルするためのもの
	CancellationTokenSource m_cts_for_HeartBeat_intvl_wait = null;

	// ------------------------------------------------------------------------------------
	public WsContext(uint idx_ws_context)
	{
		m_idx_ws_context = idx_ws_context;
	}

	// ------------------------------------------------------------------------------------
	// Abort_WS_Context() は、WS_Spawn_Context() の最初の await 以降にコールされることを想定している
	// 上記の場合、m_cts_for_ReadAsync_ws_ns と m_cts_for_HeartBeat_intvl_wait は生成済みとなっている

	public void Abort_WS_Context()
	{
		if (m_cts_for_ReadAsync_ws_ns.IsCancellationRequested == false) { m_cts_for_ReadAsync_ws_ns.Cancel(); }
		if (m_cts_for_HeartBeat_intvl_wait.IsCancellationRequested == false) { m_cts_for_HeartBeat_intvl_wait.Cancel(); }
	}

	// ------------------------------------------------------------------------------------
	public async Task WS_Spawn_Context(NetworkStream ns_WS, string str_ws_accept_key)
	{
		ms_iLog.WrtLine($"--- html : {m_idx_ws_context} -> Upgrade to WebSocket");
		m_ns_ws = ns_WS;

		using (MemBlk mem_blk_WS = ms_mem_blk_pool_WS.Lease_MemBlk())
		using (m_cts_for_ReadAsync_ws_ns = new CancellationTokenSource())
		using (m_cts_for_HeartBeat_intvl_wait = new CancellationTokenSource())
		using (m_sem_for_ns_ws_write = new SemaphoreSlim(1))
		try
		{
			// メモリバッファは、Recv と Send で兼用できる。（必ず交互にアクセスするため。ハートビート送信用は別枠にある）
			byte[] ary_buf_ws = mem_blk_WS.Get_ary_buf();
			m_ws_Recv_buf = new WS_Recv_Buf(ary_buf_ws);
			m_ws_Send_buf = new WS_Send_Buf(ary_buf_ws);

			// --------------------------------------------------------
			// WebSocket のオープン処理
			int bytes_accept_response = m_ws_Send_buf.Set_WS_Accept_Response(str_ws_accept_key);
			await m_ns_ws.WriteAsync(ary_buf_ws, 0, bytes_accept_response);

			// WebSocket をオープンしたため、ハートビートを起動する
			Task task_heart_beat = Task.Run(this.WS_HeartBeat);

			// --------------------------------------------------------
			// WebSocket による通信開始
			for (;;)
			{
				if (Server.Is_in_ShuttingDown() || mb_failed_heart_beat) { break; }  // for (;;)

				// ws の受信、または、ハートビートの受信
				int bytes_recv = await m_ns_ws.ReadAsync(ary_buf_ws, 0, Common.EN_bytes_WS_Buf, m_cts_for_ReadAsync_ws_ns.Token);
				ms_iLog.WrtLine($"--- ws 受信 : {m_idx_ws_context} -> {bytes_recv} bytes");

				if (bytes_recv == 0) { break; }  // 相手側がクローズした場合
				if (Server.Is_in_ShuttingDown() || mb_failed_heart_beat) { break; }  // for (;;)

				// ハートビートの処理
				if (ary_buf_ws[0] == 0x8a)
				{
					mb_receive_heart_beat_on_ws_spawn_context_thread = true;
					ms_iLog.WrtLine($"--- ws : {m_idx_ws_context} -> Receive HeartBeat");

					// ############################
					if (bytes_recv > 6)
					{ throw new Exception("!!! WS 受信パケットの連続処理は未実装。"); }

					continue;  // for (;;)
				}

				// クライアントからの ws のクローズ依頼処理（標準ではないはず）
				if (ary_buf_ws[0] == 0x88)
				{
					ms_iLog.WrtLine($"--- ws : {m_idx_ws_context} -> クライアントから Close opcode を受信しました。");
					break;  // for (;;)
				}

				m_ws_Recv_buf.Prepare_Read(bytes_recv);

				for (;;)  // (A)
				{
					var (ws_id, b_complete) = m_ws_Recv_buf.Read();
					switch (ws_id)
					{
					case WS_ID.EN_none: break;

					case WS_ID.EN_Pong:
						mb_receive_heart_beat_on_ws_spawn_context_thread = true;
						ms_iLog.WrtLine($"--- ws : {m_idx_ws_context} -> Receive HeartBeat");
						break;
				
					case WS_ID.EN_Close: goto FININSH_WS_ReadAsync;

					case WS_ID.EN_Invalid: goto FININSH_WS_ReadAsync;

					default:
						throw new Exception("!!! 不明な WS_ID を検出しました。");
					}

					if (b_complete == true) { break; }  // for (;;) (A)
				}
			} // for (;;)

FININSH_WS_ReadAsync:;
			if (m_cts_for_HeartBeat_intvl_wait.IsCancellationRequested == false) { m_cts_for_HeartBeat_intvl_wait.Cancel(); }
			await task_heart_beat;
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
		} // using, try

		ms_iLog.WrtLine($"--- ws : {m_idx_ws_context} -> context を終了しました。");
	}

	// ------------------------------------------------------------------------------------
	const int NUM_minu_HeartBeat_intvl = 3;  // 現在は３分としている
	const int NUM_msec_HeartBeat_intvl = NUM_minu_HeartBeat_intvl * 60 * 1000;
//	const int NUM_msec_HeartBeat_intvl = 10_000;

	// ハートビートの返信を １０秒間待つ
	// WS_Spawn_Context スレッドとの排他制御をするため、あまり長時間の待機はしない
	const int NUM_msec_wait_for_receive_HeartBeat = 10_000;

	async Task WS_HeartBeat()
	{
		try
		{
			for (;;)
			{
				// ハートビート interval wait
				await Task.Delay(NUM_msec_HeartBeat_intvl, m_cts_for_HeartBeat_intvl_wait.Token);

				m_sem_for_ns_ws_write.Wait();

				ms_iLog.WrtLine($"--- ws : {m_idx_ws_context} -> Send HeartBeat");
				await m_ns_ws.WriteAsync(ms_heart_beat_send_buf, 0, 2);
				// 以下のように、ReadAsync もここでやりたいけど、WS_Spawn_Context スレッドの方で、
				// ReadAsync の待機をしてるため、ReadAsync はここでできない。
//				await m_ns_ws.ReadAsync(ma_heart_beat_recv_buf, 0, EN_bytes_heart_beat_recv_buf, cts.Token);

				// ハートビート 受信 wait
				await Task.Delay(NUM_msec_wait_for_receive_HeartBeat);
				if (mb_receive_heart_beat_on_ws_spawn_context_thread == false) { break; } // for (;;)

				// true となっているフラグのクリア
				mb_receive_heart_beat_on_ws_spawn_context_thread = false;
			}
		}
		// ここでは、特に何もすることがない。例外を外に出さないようにするのみ
		catch (Exception) {}
		finally
		{
			if (m_sem_for_ns_ws_write.CurrentCount == 0) { m_sem_for_ns_ws_write.Release(); }
		}

		// ハートビートに失敗 or m_cts_for_Waiting_HeartBeat でキャンセル or ネットワークエラーのときに、ここにくる
		mb_failed_heart_beat = true;

		if (m_cts_for_ReadAsync_ws_ns.IsCancellationRequested == false) { m_cts_for_ReadAsync_ws_ns.Cancel(); }
	}

	// ==================================================
	// ログ用
	static ILog ms_iLog = null;

	[System.Runtime.CompilerServices.ModuleInitializer]
	public static void Module_Init() => ms_iLog = Program.Get_iLog();
}

/////////////////////////////////////////////////////////////////////////////////////////

class WS_Recv_Buf
{
	readonly public byte[] ma_buf;
	int m_idx_byte_buf = 0;
	int m_bytes_recv = 0;

	public WS_Recv_Buf(in byte[] ws_buf)
	{
		ma_buf = ws_buf;
	}

	// ------------------------------------------------------------------------------------
	public void Prepare_Read(int bytes_recv)
	{
		m_idx_byte_buf = 0;
		m_bytes_recv = bytes_recv;
	}

	// ------------------------------------------------------------------------------------
	public enum WS_ID
	{
		EN_Pong,
		EN_Close,
		EN_none,  // 通常処理を終えて、特に通知することがない場合
		EN_Invalid,  // 不正な ws パケットを送ってきたため、相手を切断する
	}

	// ------------------------------------------------------------------------------------
	// bool -> Read が「完了した場合 true」

	public unsafe (WS_ID, bool) Read()
	{
#if false
		fixed (byte* DBG_pTop_buf = ma_buf)
		{
			string str = Tools.ByteBuf_toString(DBG_pTop_buf, m_bytes_recv);
			Console.WriteLine("raw (masked)-> " + str);
		}
#endif
		int len = ma_buf[m_idx_byte_buf + 1] & 0x7f;  // MASK ビットは消しておく

		switch (ma_buf[m_idx_byte_buf])
		{
		// Pong の処理
		case 0x8a:
			if (len >= 126)
			{
				Console.WriteLine($"!!! 不正な WS パケットを受信しました。pong で len -> {len}");
				return (WS_ID.EN_Invalid, true);
			}

			m_idx_byte_buf += 2 + len;
			if (m_idx_byte_buf == m_bytes_recv)
			{ return (WS_ID.EN_Pong, true); }
			else
			{ return (WS_ID.EN_Pong, false); }
			
		// Close の処理
		case 0x88:
			return (WS_ID.EN_Close, true);

		// バイナリフレームの処理
		case 0x82:
//		case 0x81:  // 実運用時には、テキストフレームはエラーとするように変更する
			break;

		default:
			Console.WriteLine($"!!! 不正な WS パケットを受信しました。opcode -> {ma_buf[m_idx_byte_buf]:x2}");
			return (WS_ID.EN_Invalid, true);
		}

		// ------------------------------------------------------
		fixed (byte* pTop_buf = ma_buf)
		{
			// まず、length の処理をする
			//【注意】EN_bytes_WS_Buf を超えるデータを送ってくることはないようにしているはず
			byte* pbuf = pTop_buf + m_idx_byte_buf + 2;  // opcode と len の２バイトは、読み込み済み

			// ------------------------------------------------------
			// ペイロード長の取得
			if (len >= 126)
			{
				if (len == 126)
				{
					// ビッグエンディアン
					len = *pbuf * 256 + *(pbuf + 1);
					pbuf += 2;

					// -8 は、ヘッダの 4 bytes と マスクキー の 4 bytes を引く必要があるため。
					if (len > Common.EN_bytes_WS_Buf - 8)
					{
						Console.WriteLine($"!!! 不正な長さの WS パケットを受信しました。ペイロード長 -> {len} bytes");
						return (WS_ID.EN_Invalid, true);
					}
				}
				else
				// len == 127 のときの処理
				{
					Console.WriteLine($"!!! 不正な長さの WS パケットを受信しました。ペイロード長が 16bits を超えています。");
					return (WS_ID.EN_Invalid, true);
				}
			}

			// ------------------------------------------------------
			// マスクを外す処理
			byte* pTmnt_payload = pbuf + 4 + len;  // +4 は、マスクキーの分

			Unmask_Payload(pbuf, len);
			pbuf += 4;  // マスクキーの分を読み飛ばす

			// ++++++++++++++++++++++++++++++
			{
				string str = Tools.ByteBuf_toString(pbuf, len);
				Console.WriteLine("unmasked -> " + str);

//				str = Encoding.UTF8.GetString(pbuf, len);
//				Console.WriteLine("unmasked -> " + str);
			}

			Payload.Read(pbuf, len);
		}

		return (WS_ID.EN_none, true);
	}

	// ------------------------------------------------------------------------------------
	// pbuf には、ws パケットのマスクキーのアドレスを渡す

	static unsafe void Unmask_Payload(byte* pbuf, in int len)
	{
		uint mask_key = *(uint*)pbuf;
		pbuf += 4;

		for (int i = len >> 2; i-- > 0; )
		{
			*(uint*)pbuf = *(uint*)pbuf ^ mask_key;
			pbuf += 4;
		}

		switch (len & 3)
		{
		case 0: return;
		case 1:
			mask_key &= 0x0000_00ff;
			break;
		case 2:
			mask_key &= 0x0000_ffff;
			break;
		case 3:
			mask_key &= 0x00ff_ffff;
			break;
		}

		*(uint*)pbuf = *(uint*)pbuf ^ mask_key;
	}

	// ------------------------------------------------------------------------------------
	// m_idx_byte_buf の場所から、行末までの文字列を取得する

	public unsafe string DBG_Get_1Line()
	{
		if (m_idx_byte_buf == m_bytes_recv) { return ""; }

		fixed (byte* pTop_byte_buf = ma_buf)
		{
			byte* pTmnt_byte_buf = pTop_byte_buf + m_bytes_recv;

			byte* pStart_search =  pTop_byte_buf + m_idx_byte_buf;
			byte* pbyte = pStart_search;
			{
				byte chr = *pbyte;
				if (chr == 0x0d || chr == 0x0a) { return ""; }
			}
			// 上の操作により、有効文字が１文字以上あることが確定される。
			
			for (;;)
			{
				if (++pbyte == pTmnt_byte_buf) { break; }

				byte chr = *pbyte;
				if (chr == 0x0d || chr == 0x0a) { break; }
			}

			return Encoding.UTF8.GetString(pStart_search, (int)(pbyte - pStart_search));
		}
	}
}

/////////////////////////////////////////////////////////////////////////////////////////

class WS_Send_Buf
{
	readonly public byte[] ma_buf;
	int m_idx_byte_buf = 0;

	public WS_Send_Buf(in byte[] ws_buf)
	{
		ma_buf = ws_buf;
	}

	// ------------------------------------------------------------------------------------
	// 戻り値は、送信すべきバイト数

	static readonly byte[] ms_utf8_accept_http_header = Encoding.UTF8.GetBytes(
		"HTTP/1.1 101 Switching Protocols\r\n"
		+ "Upgrade: websocket\r\n"
		+ "Connection: Upgrade\r\n"
		+ "Sec-WebSocket-Accept: ");

	public unsafe int Set_WS_Accept_Response(in string str_accept_key)
	{
		fixed (byte* pTop_dst = ma_buf)
		fixed (byte* pTop_utf8_http_header = ms_utf8_accept_http_header)
		fixed (char* pTop_accept_key = str_accept_key)
		{
			byte* pdst = pTop_dst;
			{
				byte* pTmnt_utf8_http_header = pTop_utf8_http_header + ms_utf8_accept_http_header.Length;
				byte* psrc_utf8 = pTop_utf8_http_header;
				for (int i = ms_utf8_accept_http_header.Length >> 3; i-- > 0; )
				{
					*(ulong*)pdst = *(ulong*)psrc_utf8;
					pdst += 8;
					psrc_utf8 += 8;
				}

				for (; psrc_utf8 < pTmnt_utf8_http_header;)
				{
					*pdst++ = *psrc_utf8++;
				}
			}

			// accept-key の付加
			{
				char* psrc_accept_str = pTop_accept_key;
				for (int i = 28; i-- > 0; )
				{ *pdst++ = (byte)*psrc_accept_str++; }
			}
			*(uint*)pdst = 0x0a0d0a0d;

			m_idx_byte_buf = (int)(pdst - pTop_dst) + 4;

//			Console.WriteLine("+++ Accept HttpHeader");
//			Console.WriteLine(Encoding.UTF8.GetString(pTop_dst, m_idx_byte_buf));
		}
		
		return m_idx_byte_buf;
	}
}
