using System;
using System.Threading.Tasks;
using System.Text;
using System.Net;
using System.Net.Sockets;

#pragma warning disable IDE0063  // IDE0063: using を単純な形で書くことができます。
#pragma warning disable CA1835  // CA1835: ReadAsync(), WriteAsync() を、ReadOnlyMemory<> 呼び出しに変更する

class Program
{
	// --------------------------------------------------
	static ILog ms_iLog = null;
	public static ILog Get_iLog()
	{
		if (ms_iLog == null) { ms_iLog = new SLog(); }
		return ms_iLog;
	}

	// --------------------------------------------------
	static Rslt_Log ms_rslt_Log = null;
	public static IRslt_Log Get_rslt_Log()
	{
		if (ms_rslt_Log == null) { ms_rslt_Log = new Rslt_Log(); }
		return ms_rslt_Log;
	}

	// --------------------------------------------------
	static async Task Main()
//	static async Task Main(string[] args)
	{
		await Server.Start();

		ms_rslt_Log.Output_Rslt();

//		await TestClient.Get("codeshare.pgw.jp", 22225, "/test");
//		await TestClient.Get("google.com", 80, "/test");

//		WS_Recv_Buf.Test_Get_WS_Key();
	}
} // class Program

/////////////////////////////////////////////////////////////////////////////////////////

static class TestClient
{
	public static async Task Get(string str_host, int num_port, string path)
	{
		string str_GET_header = "GET " + path + " HTTP/1.1\r\n"
			+ "Host: codeshare.pgw.jp\r\n"
			+ "User-Agent: Mozilla/5.0\r\n"
			+ "Accept: text/html\r\n"
			+ "Connection: keep-alive\r\n\r\n";

		byte[] arybuf_send = Encoding.UTF8.GetBytes(str_GET_header);
		byte[] arybuf_read = new byte[4096];

		using (var tcp_client = new TcpClient(str_host, num_port))
		using (var ns = tcp_client.GetStream())
		try
		{
			await ns.WriteAsync(arybuf_send, 0, arybuf_send.Length);

			int bytes_recv = await ns.ReadAsync(arybuf_read, 0, 4096);

			Console.WriteLine(Encoding.UTF8.GetString(arybuf_read, 0, bytes_recv));
		}
		catch(Exception ex)
		{
			Console.WriteLine(ex.ToString());
		}
		finally
		{
			ns.Close();
			tcp_client.Close();
		}
	}
}

/////////////////////////////////////////////////////////////////////////////////////////

static class UTF8_str
{
	// ------------------------------------------------------------------------------------
	public unsafe static uint AsciiStr4_to_uint(in string str4)
	{
		// エラー顕在化
		if (str4.Length != 4)
		{ throw new Exception("UTF8_str.AsciiStr4_to_uint() : str4.Length != 4"); }

		fixed (char* pTop_str = str4)
		{
			byte* pTop_byte_str = (byte*)pTop_str;

			return (((uint)*(pTop_byte_str + 6)) << 24)
				+ (((uint)*(pTop_byte_str + 4)) << 16)
				+ (((uint)*(pTop_byte_str + 2)) << 8)
				+ ((uint)*pTop_byte_str);
		}
	}

	// ------------------------------------------------------------------------------------
	public unsafe static ulong AsciiStr8_to_ulong(in string str8)
	{
		// エラー顕在化
		if (str8.Length != 8)
		{ throw new Exception("UTF8_str.AsciiStr4_to_uint() : str8.Length != 8"); }

		fixed (char* pTop_str = str8)
		{
			byte* pTop_byte_str = (byte*)pTop_str;

			return (((ulong)*(pTop_byte_str + 14)) << 56)
				+ (((ulong)*(pTop_byte_str + 12)) << 48)
				+ (((ulong)*(pTop_byte_str + 10)) << 40)
				+ (((ulong)*(pTop_byte_str + 8)) << 32)
				+ (((ulong)*(pTop_byte_str + 6)) << 24)
				+ (((ulong)*(pTop_byte_str + 4)) << 16)
				+ (((ulong)*(pTop_byte_str + 2)) << 8)
				+ ((ulong)*pTop_byte_str);
		}
	}

	// ------------------------------------------------------------------------------------
	// str_src が ascii 文字列であったとして、ary_dst に idx_start から文字列をストアする
	// 戻り値は、ary_dst の次の書き込み位置

	public unsafe static int Store_str_to(in byte[] ary_dst, in int idx_start, in string str_src)
	{
		if (ary_dst.Length < idx_start + str_src.Length)
		{ throw new Exception("UTF8_str.Store_str_to() : ary_dst のバッファサイズが不足しています。"); }

		fixed (byte* pTop_dst = ary_dst)
		fixed (char* pTop_src = str_src)
		{
			byte* pdst_byte = pTop_dst + idx_start;
			char* psrc_char = pTop_src;

			for (byte* pTmnt_byte = pdst_byte + str_src.Length; pdst_byte < pTmnt_byte; )
			{
				*pdst_byte++ = (byte)*psrc_char++;  // 下位８bit のみ書き込む
			}
		}

		return idx_start + str_src.Length;
	}
}

/////////////////////////////////////////////////////////////////////////////////////////

static class Tools
{
	// ------------------------------------------------------------------------------------
	public unsafe static string ByteBuf_toString(byte* psrc, int bytes)
	{
		if (bytes == 0) { return ""; }

		string ret_str = new(' ', bytes * 3 - 1);

		fixed (char* pTop_ret_str = ret_str)
		{
			char* pdst = pTop_ret_str;
			for (; bytes-- > 0; )
			{
				char chr = (char)(*psrc >> 4);
				if (chr < 10) { *pdst = (char)(chr + 0x30); }
				else { *pdst = (char)(chr + 0x57); }

				chr = (char)(*psrc & 0x0f);
				if (chr < 10) { *(pdst + 1) = (char)(chr + 0x30); }
				else { *(pdst + 1) = (char)(chr + 0x57); }

				psrc++;
				pdst += 3;
			}
		}
		return ret_str;
	}

	// ------------------------------------------------------------------------------------
	public class Boxed<T>
	{
		public Boxed(T init_val) { m_val = init_val; }
		public T m_val;
	}
}
