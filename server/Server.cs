using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

/////////////////////////////////////////////////////////////////////////////////////////

static class Server
{
	static TcpListener ms_TcpListener_html;

	static Tools.Boxed<bool> msb_Is_in_ShuttingDown = new (false);
	public static bool Is_in_ShuttingDown() => msb_Is_in_ShuttingDown.m_val;

	const int EN_bytes_Html_Recv_Buf = 4096;
	static MemBlk_Pool ms_mem_blk_pool_Html = null;

	// EN_bytes_WS_Buf は、setting.cs で定義している
	// mem_blk は、read と write で兼用する（ハートビートの送信用は別枠）
	static MemBlk_Pool ms_mem_blk_pool_WS = null;

	// =====================================================
	struct HtmlContextInfo
	{
		public HtmlContext m_html_context;
		public Task m_task_html_context;

		public HtmlContextInfo(in HtmlContext html_context, in Task task)
		{
			m_html_context = html_context;
			m_task_html_context = task;
		}
	}

	static SortedList<uint, HtmlContextInfo> ms_List_HtmlContextInfo = new ();
	static SemaphoreSlim ms_semph_List_HtmlContextInfo = null;
	// =====================================================

	static List<WsContext> ms_List_WsContext = new ();
	static SemaphoreSlim ms_semph_List_WsContext = null;

	// ------------------------------------------------------------------------------------
	public static async Task Start()
	{
		ms_mem_blk_pool_Html = new(EN_bytes_Html_Recv_Buf, "Html_Recv_Buf");
		HtmlContext.Set_mem_blk_pool(ms_mem_blk_pool_Html);

		// WS ペイロードのマスクを外す処理を簡易化するために、「+3」としている。
		ms_mem_blk_pool_WS = new(Common.EN_bytes_WS_Buf + 3, "WS_Recv_Buf");
		WsContext.Set_mem_blk_pool(ms_mem_blk_pool_WS);

		// Server.Close() により停止させるとき、ソケットをクローズさせるためにメンバ変数として記録している
		ms_TcpListener_html = new TcpListener(IPAddress.Parse(Setting.EN_LocalAddr_for_server), Setting.NUM_html_port);
		uint idx_html_context = 0;

		using (ms_semph_List_HtmlContextInfo = new SemaphoreSlim(1))
		using (ms_semph_List_WsContext = new (1))
		try
		{
			ms_TcpListener_html.Start();
			ms_iLog.WrtLine("--- codeshare サーバーが起動しました。");

			for (;;)
			{
				// AcceptTcpClientAsync() はキャンセルトークンに対応するメソッドを持っていない
				TcpClient tcp_client = await ms_TcpListener_html.AcceptTcpClientAsync();

				HtmlContext html_context = new (tcp_client, ++idx_html_context);
				Task task_html_context = html_context.Spawn_HtmlContext();

				// ms_semph_List_HtmlContextInfo によるデッドロックの回避措置
				lock (msb_Is_in_ShuttingDown)
				{
					if (msb_Is_in_ShuttingDown.m_val == false)
					{
						ms_semph_List_HtmlContextInfo.Wait();
						ms_List_HtmlContextInfo.Add(idx_html_context, new HtmlContextInfo(html_context, task_html_context));
						ms_semph_List_HtmlContextInfo.Release();

						continue;
					}
				}
				
				// ThreadProc_ServerClose_by_WS_Context() を実行している場合の処理
				ms_iLog.WrtLine("--- HtmlContext を停止させます。idx_html_context -> " + idx_html_context);
				html_context.Abort_WS_Context_if_exists();

				await task_html_context;
				ms_iLog.WrtLine("--- HtmlContext が停止しました。idx_html_context -> " + idx_html_context);
				break;  // for (;;)
			}
		}
		catch (SocketException ex)
		{
			if (ex.ErrorCode == 995)
			{
				// 995 : スレッドの終了またはアプリケーションの要求によって、I/O処理は中止されました。
				// ms_TcpListener_html.Server.Close(); による例外送出
				goto FINISH_SocketException;
			}
			else
			{
				ms_iLog.Wrt_Warning_Line(
					$"!!! ms_TcpListener_html.AcceptTcpClientAsync() ループ処理中に例外を検出しました。\r\n{ex}");
			}

FINISH_SocketException:;
		}
		catch (Exception ex)
		{
			ms_iLog.Wrt_Warning_Line(
				$"!!! ms_TcpListener_html.AcceptTcpClientAsync() ループ処理中に例外を検出しました。\r\n{ex}");
		}
	}

	// ------------------------------------------------------------------------------------
	public static void Remove_HtmlContextInfo(in uint idx_html_context)
	{
		// ms_semph_List_HtmlContextInfo によるデッドロックの回避措置
		lock (msb_Is_in_ShuttingDown)
		{
			if (msb_Is_in_ShuttingDown.m_val == false)
			{
				ms_semph_List_HtmlContextInfo.Wait();
				ms_List_HtmlContextInfo.Remove(idx_html_context);
				ms_semph_List_HtmlContextInfo.Release();
			}
		}
	}

	// ------------------------------------------------------------------------------------
	// このメソッドは、ws 通信によってのみ起動される
	// 起動するのは１回のみ

	public static async void ThreadProc_ServerClose_by_WS_Context()
	{
		lock (msb_Is_in_ShuttingDown)
		{
			// msb_Is_in_ShuttingDown が true になるのは、ここ１ヶ所のみ
			msb_Is_in_ShuttingDown.m_val = true;
		}

		try
		{
			ms_semph_List_HtmlContextInfo.Wait();

			ms_iLog.WrtLine("\r\n\r\n=== Server.Close() がコールされました。===\r\n");

			foreach (var kvp in ms_List_HtmlContextInfo)
			{
				ms_iLog.WrtLine("--- HtmlContext を停止させます。idx_html_context -> " + kvp.Key);

				HtmlContextInfo context_info = kvp.Value;
				context_info.m_html_context.Abort_WS_Context_if_exists();

				await context_info.m_task_html_context;
				ms_iLog.WrtLine("--- HtmlContext が停止しました。idx_html_context -> " + kvp.Key);
			}

			ms_semph_List_HtmlContextInfo.Release();
		}
		catch (Exception ex)
		{
			ms_iLog.Wrt_Warning_Line($"!!! ThreadProc_ServerClose_by_WS_Context() で例外を検出しました。\r\n{ex}");
		}
		finally
		{
			if (ms_semph_List_HtmlContextInfo.CurrentCount == 0) { ms_semph_List_HtmlContextInfo.Release(); }
			ms_List_HtmlContextInfo.Clear();

			ms_TcpListener_html.Server.Close();
		}
	}

	// ------------------------------------------------------------------------------------
	public static void Add_WsContext(WsContext ws_context_to_add)
	{
	}

	// ==================================================
	// ログ用
	static ILog ms_iLog = null;
//	static IRslt_Log ms_Rslt_Log = null;
/*
	public static string Rslt_Log_CB_Proc_on_AppEnd()
	{
	}
*/
	[System.Runtime.CompilerServices.ModuleInitializer]
	public static void Module_Init()
	{
		ms_iLog = Program.Get_iLog();
//		ms_Rslt_Log = Program.Get_rslt_Log();
	}
}

/////////////////////////////////////////////////////////////////////////////////////////

class StaticFiles
{
	public static byte[] msa_index_html;
	public static byte[] msa_client_js;
	public static byte[] msa_styles_css;

	public static byte[] msa_404_not_found;

	// ------------------------------------------------------------------------------------
	[System.Runtime.CompilerServices.ModuleInitializer]
	public static void Module_Init()
	{
		string str_index_html_header = "HTTP/1.1 200 OK\r\n"
			+ "Cache-Control: public, max-age=604800, immutable\r\n"
			+ "Content-Type: text/html; charset=UTF-8\r\n"
			+ "Connection: keep-alive\r\n"
			+ $"Keep-Alive: timeout={Setting.NUM_seconds_html_keep_alive}\r\n"
			+ "Content-Length: ";

		string str_client_js_header = "HTTP/1.1 200 OK\r\n"
			+ "Cache-Control: public, max-age=604800, immutable\r\n"
			+ "Content-Type: application/javascript; charset=UTF-8\r\n"
			+ "Connection: keep-alive\r\n"
			+ $"Keep-Alive: timeout={Setting.NUM_seconds_html_keep_alive}\r\n"
			+ "Content-Length: ";

		string str_styles_css_header = "HTTP/1.1 200 OK\r\n"
			+ "Cache-Control: public, max-age=604800, immutable\r\n"
			+ "Content-Type: text/css; charset=UTF-8\r\n"
			+ "Connection: keep-alive\r\n"
			+ $"Keep-Alive: timeout={Setting.NUM_seconds_html_keep_alive}\r\n"
			+ "Content-Length: ";
			
		msa_index_html = Crt_ResBytes(str_index_html_header, Setting.EN_public_folder + "\\index.html");
		msa_client_js = Crt_ResBytes(str_client_js_header, Setting.EN_public_folder + "\\client.js");
		msa_styles_css = Crt_ResBytes(str_styles_css_header, Setting.EN_public_folder + "\\styles.css");

		// ------------------------------------
		string str_404_header = "HTTP/1.1 404 Not Found\r\n"
			+ "Content-Type: text/html; charset=UTF-8\r\n"
			+ "Content-Length: ";

		msa_404_not_found = Crt_ResBytes(str_404_header, Setting.EN_public_folder + "\\404.html");
	}

	// ------------------------------------------------------------------------------------
	static byte[] Crt_ResBytes(in string str_header, in string file_path)
	{
		FileInfo file_info = new(file_path);
		long bytes_file = file_info.Length;
		string str_bytes_file = bytes_file.ToString("D");

		byte[] ret_ary = new byte[str_header.Length + str_bytes_file.Length + 4 + bytes_file];

		int idx_ret_ary = UTF8_str.Store_str_to(ret_ary, 0, str_header);
		idx_ret_ary = UTF8_str.Store_str_to(ret_ary, idx_ret_ary, str_bytes_file);
		idx_ret_ary = UTF8_str.Store_str_to(ret_ary, idx_ret_ary, "\r\n\r\n");

		using (FileStream fs = file_info.OpenRead())
		{
			fs.Read(ret_ary, idx_ret_ary, (int)file_info.Length);
		}

		return ret_ary;
	}
}
