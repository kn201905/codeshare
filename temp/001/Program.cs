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
			await Server.Start();
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
				TcpClient tcp_client = await ms_TcpListener_http.AcceptTcpClientAsync();

				Console.WriteLine("--- 接続を検知しました ---");
			}
		}
	}
}
