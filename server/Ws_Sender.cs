using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Text;

#pragma warning disable CA1835  // CA1835: ReadAsync(), WriteAsync() を、ReadOnlyMemory<> 呼び出しに変更する

partial class WsContext
{

/////////////////////////////////////////////////////////////////////////////////////////
// WsContext のプライベートサブクラス

class Ws_Sender
{
	readonly NetworkStream m_ns_ws;

	public readonly byte[] ma_sendr_buf;  // sendr_buf の先頭４bytes は、常に WSヘッダ用に利用される
	readonly int m_bytes_sendr_buf;
	int m_sendr_pos_unused = 4;  // WSヘッダ用に４bytes 利用する
	// デバッグ中
//	public int m_sendr_pos_unused = 4;  // WSヘッダ用に４bytes 利用する

	// ------------------------------------------------------------------------------------
	public Ws_Sender(in NetworkStream ns_for_send, in byte[] sendr_buf)
	{
		m_ns_ws = ns_for_send;

		ma_sendr_buf = sendr_buf;
		m_bytes_sendr_buf = sendr_buf.Length;
	}

	// ------------------------------------------------------------------------------------
	// これは、WS ストリームが起動する前に利用されるメソッドのため、スレッド排他の機構は必要ない

	static readonly byte[] ms_utf8_accept_http_header = Encoding.UTF8.GetBytes(
		"HTTP/1.1 101 Switching Protocols\r\n"
		+ "Upgrade: websocket\r\n"
		+ "Connection: Upgrade\r\n"
		+ "Sec-WebSocket-Accept: ");

	public async Task Send_WS_Accept_Response(string str_accept_key)
	{
		int bytes_to_send;
		unsafe
		{
			fixed (byte* pTop_sendr_buf = ma_sendr_buf)
			fixed (byte* pTop_utf8_http_header = ms_utf8_accept_http_header)
			fixed (char* pTop_accept_key = str_accept_key)
			{
				byte* pdst = pTop_sendr_buf;
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

				bytes_to_send = (int)(pdst - pTop_sendr_buf) + 4;  // +4 : \r\n\r\n
			}
		}

		await m_ns_ws.WriteAsync(ma_sendr_buf, 0, bytes_to_send);
	}

	// ------------------------------------------------------------------------------------
	// 必要量が確保できないときは、-1 が返される
	// par_additional は、基本的に ds_idx が想定されている

	public int Alloc_Raw_Buf(int bytes)
	{
		// エラー顕在化
		if (bytes > m_bytes_sendr_buf)
		{ throw new Exception("!!! WS_Sender.Get_pBuf() : bytes > m_bytes_buf"); }

		if (bytes > m_bytes_sendr_buf - m_sendr_pos_unused) { return -1; }

		// バッファを確保
		int ret_pos = m_sendr_pos_unused;
		m_sendr_pos_unused += bytes;

		return ret_pos;
	}

	// ------------------------------------------------------------------------------------
	// bytes_payload は、cmdID も含めたバイト数（２以上となるはず）
	// 戻り値の pos は、cmdID, par_additional の「次の」pos となるため注意（pos_inner）

	public int Alloc_Buf_withID(in CS_ID cmdID, in int bytes_payload, in byte par_additional = 0)
	{
		// エラー顕在化
		if (bytes_payload > m_bytes_sendr_buf)
		{ throw new Exception("!!! WS_Sender.Get_pBuf() : bytes > bytes_payload"); }

		if (bytes_payload > m_bytes_sendr_buf - m_sendr_pos_unused) { return -1; }

		// ----------------------------------------
		int ret_pos = m_sendr_pos_unused + 2;

		ma_sendr_buf[m_sendr_pos_unused] = (byte)cmdID;
		ma_sendr_buf[m_sendr_pos_unused + 1] = par_additional;

		m_sendr_pos_unused += bytes_payload;
		return ret_pos;
	}

	// ------------------------------------------------------------------------------------
	// WS ヘッダを書き加えて、WS で送信する
	// WriteTimeout で、例外が出されることを常に考慮する必要がある
	
	public void Send_by_WS()  // リターンに時間が掛かる場合があることに留意すること
	{
		// エラー顕在化（先頭４bytes は Websocket ヘッダ用。続く２bytes は cmdID、par_additional）
		if (m_sendr_pos_unused <= 5)
		{ throw new Exception("!!! WS_Sender.Send_by_WS() : m_sendr_pos_unused <= 5"); }

		int pos_top_to_send;
		unsafe
		{
			fixed (byte* pTop_buf = ma_sendr_buf)
			{
				uint len_payload = (uint)(m_sendr_pos_unused - 4);
				if (len_payload < 126)
				{
					pos_top_to_send = 2;
					*(pTop_buf + 2) = 0x82;  // WS payload は、バイナリフレーム
					*(pTop_buf + 3) = (byte)len_payload;
				}
				else
				{
					pos_top_to_send = 0;
					*pTop_buf = 0x82;  // WS payload は、バイナリフレーム
					*(pTop_buf + 1) = 126;
					// ushort 値をビッグエンディアンで設定する必要がある
					*(pTop_buf + 2) = (byte)(len_payload >> 8);
					*(pTop_buf + 3) = (byte)(len_payload & 0xff);
				}
			}
		}

		// CmdStream -> CmdThread を通さずに、直接該当 ns で「書き込み」をする場合があるため lock が必要となる
		lock (m_ns_ws)
		{
			m_ns_ws.Write(ma_sendr_buf, pos_top_to_send, m_sendr_pos_unused - pos_top_to_send);
		}
		m_sendr_pos_unused = 4;  // バッファを空とする（4 は websocket ヘッダ書き込み用）
	}

	// ------------------------------------------------------------------------------------
	// バッファが不足しているときのみ失敗し、false が返される。

	public bool Copy_frm_CS_Payload(uint cs_pos_payload, uint bytes_payload)
	{
		if (bytes_payload > m_bytes_sendr_buf - m_sendr_pos_unused) { return false; }

		CmdStream.NoLc_Copy_CS_buf_to(ma_sendr_buf, m_sendr_pos_unused, cs_pos_payload, bytes_payload);
		m_sendr_pos_unused += (int)bytes_payload;

		return true;
	}
}

}  // partial class WsContext
