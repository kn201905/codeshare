using System;
using System.Threading.Tasks;
using System.Net;
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
		static TcpListener ms_TcpListener_http;

		public static async Task Start()
		{
			Console.WriteLine("--- サーバーを起動します ---");

			ms_TcpListener_http = new TcpListener(System.Net.IPAddress.Parse("127.0.0.1"), 80);
			ms_TcpListener_http.Start();

			for (;;)
			{
				var task_1 = ms_TcpListener_http.AcceptTcpClientAsync();
				TcpClient tcp_client = await task_1;

				Console.WriteLine("--- 接続を検知しました ---");
			}
		}
	}
}
