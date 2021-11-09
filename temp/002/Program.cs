using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Text;
using System.Net.Sockets;

namespace codeshare
{
	class Program
	{
		static async Task Main()
		{
			Task task_2 = Server.Start();
			await task_2;
		}
	}
	
	static class Server
	{
		static TcpListener ms_server_TcpListener;

		public static async Task Start()
		{
			Console.WriteLine("--- サーバーを起動します ---");

			ms_server_TcpListener = new TcpListener(System.Net.IPAddress.Parse("127.0.0.1"), 80);
			ms_server_TcpListener.Start();

			for (;;)
			{
				var task_1 = ms_server_TcpListener.AcceptTcpClientAsync();
				TcpClient tcp_client = await task_1;

				Console.WriteLine("--- 接続を検知しました ---");

				HttpContext http_context = new HttpContext(tcp_client);
				Task task = http_context.Spawn_HttpContext();
			}
		}
	}
	
	class HttpContext
	{
	    TcpClient m_tcpClt_Http;
	    
	    public HttpContext(TcpClient tcpClt_Http)
	    {
	        m_tcpClt_Http = tcpClt_Http;
	    }
	    
		public async Task Spawn_HttpContext()
		{
			byte[] ns_buf = new byte[4000];

			using (m_tcpClt_Http)
			using (NetworkStream ns_Http = m_tcpClt_Http.GetStream())
			{
				int bytes_recv;
				using (var cts = new CancellationTokenSource(5000))
				{
					bytes_recv = await ns_Http.ReadAsync(ns_buf, 0, 4000, cts.Token);
				}
				if (bytes_recv == 0) { return; }

				string str_recv = new UTF8Encoding().GetString(ns_buf, 0, bytes_recv);
				Console.WriteLine($"+++ 受信したメッセージ\r\n{str_recv}");
			}
		}	    
	}
}
