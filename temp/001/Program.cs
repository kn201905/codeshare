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
		static TcpListener ms_server_TcpListener;

		public static async Task Start()
		{
			Console.WriteLine("--- サーバーを起動します ---");

			ms_server_TcpListener = new TcpListener(System.Net.IPAddress.Parse("127.0.0.1"), 80);
			ms_server_TcpListener.Start();

			for (;;)
			{
				TcpClient tcp_client = await ms_server_TcpListener.AcceptTcpClientAsync();

				Console.WriteLine("--- 接続を検知しました ---");
			}
		}
	}
}
