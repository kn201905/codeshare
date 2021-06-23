using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Text;
using System.Net;

#pragma warning disable CA1835  // CA1835: ReadAsync(), WriteAsync() を、ReadOnlyMemory<> 呼び出しに変更する

/////////////////////////////////////////////////////////////////////////////////////////

class HtmlContext
{
	readonly ushort m_idx_html_context;
	readonly TcpClient m_tcpClt_Html;

	bool mb_dont_create_ws_context = false;
	WsContext m_ws_context = null;  // Abort_WS_Context() をコールするために、メンバ変数としている

	static MemBlk_Pool ms_mem_blk_pool_Html = null;
	public static void Set_mem_blk_pool(MemBlk_Pool mem_blk_pool) => ms_mem_blk_pool_Html = mem_blk_pool;

	// ------------------------------------------------------------------------------------
	public HtmlContext(TcpClient tcp_client_Html, ushort idx_clt_context)
	{
		m_tcpClt_Html = tcp_client_Html;
		m_idx_html_context = idx_clt_context;
	}

	// ------------------------------------------------------------------------------------
	public void Abort_WS_Context_if_exists()
	{
		lock (this)
		{
			if (m_ws_context == null)
			{
				mb_dont_create_ws_context = true;
			}
			else
			{
				// m_ws_context != null であるとき
				// このとき、m_ws_context.WS_Spawn_Context() からの最初のリターンを受け付けている状態であるため、
				// Abort_WS_Context() をコールすることができる。
				m_ws_context.Abort_WS_Context();
			}
		}
	}

	// ------------------------------------------------------------------------------------
	// Spawn_Html_Context() の働きは以下の２通り
	// 1. index.html 等の GET リクエストの処理（この場合、send 時は静的バッファを利用するため、動的な send 用バッファは必要ない）
	// 2. WS context に upgrade する場合、WS_Context を起動し、処理をそちらの方に移管する

	public async Task Spawn_HtmlContext()
	{
		ms_iLog.WrtLine($"    html : {m_idx_html_context} --- 新規 Spawn_Context() ---");

		using (m_tcpClt_Html)
		using (NetworkStream ns_Html = m_tcpClt_Html.GetStream())
		try
		{
			string str_accept_key = null;  // websocket への upgrade のフラグも兼用している

			using (MemBlk mem_blk_Html = ms_mem_blk_pool_Html.Lease_MemBlk())
			{
				Html_Recv_Buf recv_buf_Html = new(mem_blk_Html);

				// --------------------------------------
				bool b_IsUpgrade_toWS = false;
				for (;;)
				{
					if (Server.Is_in_ShuttingDown()) { break; } // for (;;)
					int bytes_recv;
					using (var cts = new CancellationTokenSource(Setting.NUM_msec_html_keep_alive))
					{
						bytes_recv = await ns_Html.ReadAsync(recv_buf_Html.ma_byte_buf, 0, 4096, cts.Token);
					}

					if (bytes_recv == 0) { break; }  // for (;;)
					if (Server.Is_in_ShuttingDown()) { break; } // for (;;)

//					ms_iLog.WrtLine(Encoding.UTF8.GetString(recv_buf_Html.ma_byte_buf, 0, bytes_recv));

					Html_Recv_Buf.Http_Mthd_ID mthd_id = recv_buf_Html.Read_Http_Mthd(bytes_recv);

					if (mthd_id == Html_Recv_Buf.Http_Mthd_ID.EN_GET)
					{
						byte[] buf_file = recv_buf_Html.Get_file_forGET();
						if (buf_file == null)
						{
							ms_iLog.Wrt_Warning_Line("GET で不明なファイルを要求されました。\r\n"
								+ "要求されたファイル -> " + recv_buf_Html.DBG_Get_1Line());
							break;  // for (;;)
						}

						await ns_Html.WriteAsync(buf_file, 0, buf_file.Length);
						continue;  // for (;;)
					}

					if (mthd_id == Html_Recv_Buf.Http_Mthd_ID.EN_Upgrade_WS)
					{
						b_IsUpgrade_toWS = true;
						break;  // for (;;)
					}

					// 不明なメソッドに対する処理
					ms_iLog.Wrt_Warning_Line(Encoding.UTF8.GetString(recv_buf_Html.ma_byte_buf, 0, bytes_recv));
					break;  // for (;;)
				}
				// --------------------------------------

				// websocket への移行処理（ここで mem_blk_Html が有効な間に、ws-key の処理だけをしておく）
				if (b_IsUpgrade_toWS == true)
				{
					if (recv_buf_Html.Search_WS_Key() == false)
					{
						ms_iLog.Wrt_Warning_Line("websocket への Upgrade時に、「Sec-WebSocket-Key:」が見つかりませんでした。");
					}
					else
					{
						//「Sec-WebSocket-Key:」を見つけたときの処理
						str_accept_key = recv_buf_Html.Get_Accept_Key();
						if (str_accept_key == null)
						{
							ms_iLog.Wrt_Warning_Line("Get_Accept_Key() に失敗しました。");
						}
					}
				}
			}  // mem_blk_Html.Dispose()

			// websocket への upgrade context である場合の処理
			if (str_accept_key != null)
			{
				Task task_ws_context = null;
				lock (this)
				{
					if (mb_dont_create_ws_context == false)
					{
						ms_iLog.WrtLine($"\r\n--- html : {m_idx_html_context} === Upgrade to WebSocket ===\r\n"
											+ $"+++ address -> {((IPEndPoint)m_tcpClt_Html.Client.RemoteEndPoint).Address}\r\n");

						m_ws_context = new WsContext(m_idx_html_context);
						task_ws_context = m_ws_context.WS_Spawn_Context(ns_Html, str_accept_key);

						// WS_Spawn_Context() を発行しているため、この lock(this) を抜ける前に
						// WsContext の m_cts_for_ReadAsync_ws_ns, m_semph_WS_Cmd は生成済みとなっている。
					}
				}
				if (task_ws_context != null) { await task_ws_context; }
			}

      }  // 先頭の using & try
		catch (OperationCanceledException) {}  // ns_Html.ReadAsync() をキャンセルした場合（特に何もすることがない）
		catch (AggregateException ex)
		{
			ms_iLog.Wrt_Warning_Line($"!!! html : {m_idx_html_context} -> AggregateException 補足 : {ex}\r\n");
		}
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

			ms_iLog.Wrt_Warning_Line($"!!! html : {m_idx_html_context} -> System.IO.IOException 補足 : {ex}\r\n");
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
				ms_iLog.Wrt_Warning_Line($"!!! html : {m_idx_html_context} -> 例外補足 : {ex}\r\n");
			}
      }
		finally
		{
			// ++++++++++++++++++++++++++++++++++++++++++++++++++++++++++
			// TODO: 送信途中のリソースがあれば、ここで解放すること
			// Server.Is_under_send_operation() で送信途中かどうか判定できる
			// ++++++++++++++++++++++++++++++++++++++++++++++++++++++++++

			m_tcpClt_Html.Close();
			ns_Html.Close();
		} // using, try

		Server.Remove_HtmlContextInfo(m_idx_html_context);

		ms_iLog.WrtLine($"    html : {m_idx_html_context} xxx 接続を終了しました。xxx");
//    Server.Remove_frm_task_list(m_idx_clt_context);
	}

	// ==================================================
	// ログ用
	static ILog ms_iLog = null;

	[System.Runtime.CompilerServices.ModuleInitializer]
	public static void Module_Init() => ms_iLog = Program.Get_iLog();
}

/////////////////////////////////////////////////////////////////////////////////////////

class Html_Recv_Buf
{
	readonly public byte[] ma_byte_buf = null;
	int m_idx_byte_buf = 0;
	int m_bytes_recv = 0;

	public Html_Recv_Buf(MemBlk mem_blk_Html)
	{
		ma_byte_buf = mem_blk_Html.Get_ary_buf();
	}

	// ------------------------------------------------------------------------------------
	public enum Http_Mthd_ID
	{
		EN_GET,
		EN_Upgrade_WS,

		EN_Unknown,
	}

	// ------------------------------------------------------------------------------------
	// GET が見つかった場合 true が返される。m_idx_byte_buf は、"GET " の直後に設定される。
	// GET が見つからなかった場合 false が返される。m_idx_byte_buf は変更されない。

	static readonly uint ms_uistr_GET = UTF8_str.AsciiStr4_to_uint("GET ");

	public unsafe Http_Mthd_ID Read_Http_Mthd(in int bytes_recv)
	{
		m_idx_byte_buf = 0;
		m_bytes_recv = bytes_recv;

		// 最低でも、通常 18文字以上あるはず
		if (m_bytes_recv < 18) { return Http_Mthd_ID.EN_Unknown; }

		fixed (byte* pTop_buf = ma_byte_buf)
		{
			byte* pTmnt_buf = pTop_buf + m_bytes_recv - 17;
			byte* pbuf = pTop_buf;

			for (;;)
			{
				if (*(uint*)pbuf == ms_uistr_GET)
				{
					if (this.Chk_WS_Upgrade(pbuf + 4, pTop_buf) == true)
					{ return Http_Mthd_ID.EN_Upgrade_WS; }

					m_idx_byte_buf = (int)(pbuf - pTop_buf) + 4;
					return Http_Mthd_ID.EN_GET;
				}

				// pbuf を次の行の先頭へ移動させる
				for (;;)
				{
					if (*pbuf++ == 0x0a)
					{
						if (pbuf == pTmnt_buf) { return Http_Mthd_ID.EN_Unknown; }
						break;
					}
					if (pbuf == pTmnt_buf) { return Http_Mthd_ID.EN_Unknown; }
				}
			}
		}
	}

	// ------------------------------------------------------------------------------------
	// pbuf は、「GET 」の直後を指していること

	static readonly ulong ms_ulstr_Upgrade = UTF8_str.AsciiStr8_to_ulong("Upgrade:");
	static readonly ulong ms_ulstr_websocke = UTF8_str.AsciiStr8_to_ulong("websocke");

	unsafe bool Chk_WS_Upgrade(byte* pbuf, in byte* pTop_buf)
	{
		// まず、「GET / 」であるかどうかをチェック
		if (*(ushort*)pbuf != 0x202f) { return false; }  // 0x2f = '/'

		byte* pTmnt_buf = pTop_buf + m_bytes_recv - 17;
		pbuf += 2;

		for (;;)
		{
			// pbuf を次の行の先頭へ移動させる
			for (;;)
			{
				if (pbuf >= pTmnt_buf) { return false; }
				if (*pbuf++ == 0x0a)
				{
					if (pbuf == pTmnt_buf) { return false; }
					if (*pbuf == 0x0d) { return false; }  // ヘッダ終了マークを検出したとき
					break;
				}
			}

			if (*(ulong*)pbuf == ms_ulstr_Upgrade)
			{
				//「Upgrade: websocket」は、最下行にあることもあるため、m_idx_byte_buf の移動処理はしない
				if (*(ulong*)(pbuf + 9) == ms_ulstr_websocke) { return true; }

				return false;  // "websocket 以外の upgrade はないはず"
			}
		}
	}

	// ------------------------------------------------------------------------------------
	// m_idx_byte_buf は変更されない
	// 戻り値は、GET の対象となったファイルが格納されている byte[] バッファ
	// 不明なファイルが要求された場合は、null が返される

	static readonly ulong ms_ulstr_index_html = UTF8_str.AsciiStr8_to_ulong("/index.h");
	static readonly ulong ms_ulstr_client_js = UTF8_str.AsciiStr8_to_ulong("/client.");
	static readonly ulong ms_ulstr_WS_Stream_js = UTF8_str.AsciiStr8_to_ulong("/WS_Stre");
	static readonly ulong ms_ulstr_styles_css = UTF8_str.AsciiStr8_to_ulong("/styles.");
	static readonly ulong ms_ulstr_favicon = UTF8_str.AsciiStr8_to_ulong("/favicon");

	public unsafe byte[] Get_file_forGET()
	{
		fixed (byte* pTop_byte_buf = ma_byte_buf)
		{
			byte* pbyte_buf = pTop_byte_buf + m_idx_byte_buf;
			if (*(ushort*)pbyte_buf == 0x202f) { return StaticFiles.msa_index_html; }  // 0x2f = '/'

			ulong ulstr = *(ulong*)pbyte_buf;
			if (ulstr == ms_ulstr_index_html) { return StaticFiles.msa_index_html; }
			else if (ulstr == ms_ulstr_client_js) { return StaticFiles.msa_client_js; }
			else if (ulstr == ms_ulstr_WS_Stream_js) { return StaticFiles.msa_WS_Stream_js; }
			else if (ulstr == ms_ulstr_styles_css) { return StaticFiles.msa_styles_css; }
			else if (ulstr == ms_ulstr_favicon) { return StaticFiles.msa_404_not_found; }

			return null;
		}
	}

	// ------------------------------------------------------------------------------------
	// Sec-WebSocket-Key: が見つかった場合 true が返される。m_idx_byte_buf は、"Sec-WebSocket-Key:" の直後に設定される。
	// Sec-WebSocket-Key: が見つからなかった場合 false が返される。m_idx_byte_buf は変更されない。

	static readonly ulong ms_ulstr_WS_KEY_1 = UTF8_str.AsciiStr8_to_ulong("Sec-WebS");
	static readonly ulong ms_ulstr_WS_KEY_2 = UTF8_str.AsciiStr8_to_ulong("ocket-Ke");

	public unsafe bool Search_WS_Key()
	{
		// 最低でも "Sec-WebSocket-Key:" の１８文字と、さらに nonce の２４文字は必要
		if (m_bytes_recv < 42) { return false; }

		fixed (byte* pTop_buf = ma_byte_buf)
		{
			byte* pTmnt_buf = pTop_buf + m_bytes_recv - 41;

			//「Upgrade: websocket」は、最下行にあることもあるため、m_idx_byte_buf の事前処理はしないことにした
			byte* pbuf = pTop_buf;
//			byte* pbuf = pTop_buf + m_idx_byte_buf;

			for (;;)
			{
				// 行頭チェック
				if (*(ulong*)pbuf == ms_ulstr_WS_KEY_1)
				{
					if (*(ulong*)(pbuf + 8) == ms_ulstr_WS_KEY_2)
					{
						// "Sec-WebSocket-Key:" -> １８文字
						m_idx_byte_buf = (int)(pbuf - pTop_buf) + 18;
						return true;
					}

					pbuf += 8;
				}

				// pbyte_src を次の行の先頭へ移動させる
				for (;;)
				{
					// pbuf += 8; をしている場合があるため、psrc > pTmnt_src となることも考えられる。
					if (pbuf >= pTmnt_buf) { return false; }

					if (*pbuf++ == 0x0a)
					{
						if (pbuf == pTmnt_buf) { return false; }
						break;
					}
				}
			}
		}
	}

	// ------------------------------------------------------------------------------------
	// Get_Accept_Key() に失敗したときは、null が返される

	static readonly string ms_str_for_WS_hash = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
//	static readonly System.Security.Cryptography.SHA1 ms_sha1 = System.Security.Cryptography.SHA1.Create();

	public unsafe string Get_Accept_Key()
	{
		fixed (byte* pTop_buf = ma_byte_buf)
		{
			byte* pbuf = pTop_buf + m_idx_byte_buf;

			// 空白を読み飛ばす
			for (byte* pTmnt_buf = pTop_buf + m_bytes_recv - 23;;)  // nonce は２４文字
			{
				if (pbuf >= pTmnt_buf) { return null; }
				if (*pbuf != 0x20) { break; }
				pbuf++;
			}

			// SHA-1 の生成元のデータを生成
			Span<byte> sha1_src = stackalloc byte[24 + 36];
			fixed (byte* pTop_sha1_src = sha1_src)
			fixed (char* pTop_str_for_WS_hash = ms_str_for_WS_hash)
			{
				byte* pdst = pTop_sha1_src;
				for (int i = 24; i-- > 0; )
				{ *pdst++ = *pbuf++; }

				char* psrc_str_for_WS_hash = pTop_str_for_WS_hash;
				for (int i = 36; i-- > 0; )
				{ *pdst++ = (byte)*psrc_str_for_WS_hash++; }
			}

			Span<byte> sha1_dst = stackalloc byte[20];
			var sha1 = System.Security.Cryptography.SHA1.Create();
			if (sha1.TryComputeHash(sha1_src, sha1_dst, out int bytes_wrtn) == false)
			{
				Console.WriteLine("!!! SHA1 のハッシュ値生成に失敗しました。");
				return null;
			}

			return Convert.ToBase64String(sha1_dst);
		}
	}

	// ------------------------------------------------------------------------------------
	// m_idx_byte_buf の場所から、行末までの文字列を取得する

	public unsafe string DBG_Get_1Line()
	{
		if (m_idx_byte_buf == m_bytes_recv) { return ""; }

		fixed (byte* pTop_byte_buf = ma_byte_buf)
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

	// ==================================================
	// ログ用
	static ILog ms_iLog = null;

	[System.Runtime.CompilerServices.ModuleInitializer]
	public static void Module_Init() => ms_iLog = Program.Get_iLog();
}
