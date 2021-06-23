using System;
using System.Threading;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Text;

#pragma warning disable CA1835  // CA1835: ReadAsync(), WriteAsync() を、ReadOnlyMemory<> 呼び出しに変更する

partial class WsContext
{

/////////////////////////////////////////////////////////////////////////////////////////
// WsContext のプライベートサブクラス

class Ws_RecvBuf
{
	// Read() に対する応答で、自分だけのクライアントに短い応答を送る場合に利用する
	readonly NetworkStream m_ns_ws;

	readonly public byte[] ma_recv_buf;
//	int m_recv_pos_unread = 0;
	int m_depo_bytes = 0;

	readonly byte[] ma_Rea_Doc_send_buf = new byte[6];

	readonly ushort m_idx_ws_context;
	readonly CancellationTokenSource m_cts_for_ReadAsync_ws_ns;

	// ------------------------------------------------------------------------------------
	// コンストラクタ
	public Ws_RecvBuf(in NetworkStream ns_ws, in byte[] ws_buf, in ushort idx_ws_context, CancellationTokenSource cts)
	{
		m_ns_ws = ns_ws;
		ma_recv_buf = ws_buf;

		m_idx_ws_context = idx_ws_context;
		m_cts_for_ReadAsync_ws_ns = cts;

		// バイナリフレームで、payload = 4 bytes
		ma_Rea_Doc_send_buf[0] = 0x82;
		ma_Rea_Doc_send_buf[1] = 0x04;
		ma_Rea_Doc_send_buf[2] = Common.CMD_Req_Doc;
		ma_Rea_Doc_send_buf[3] = 0;
	}

	// ------------------------------------------------------------------------------------
	public void Set_bytes_recv(in int bytes_recv)
	{
//		m_recv_pos_unread = 0;
		m_depo_bytes = bytes_recv;
	}

	// ------------------------------------------------------------------------------------
	public bool Is_Complete_toRead()
	{
		return (m_depo_bytes == 0);
	}

	// ------------------------------------------------------------------------------------
	// 戻り値： WebSocket の通信を維持するかどうか。false ならば、WebSocket を閉じる。
	
	const byte EN_WebSocket_Pong = 0x8a;
	const byte EN_WebSocket_Close = 0x88;
	const byte EN_WebSocket_Binary = 0x82;

//	[System.Runtime.CompilerServices.SkipLocalsInit]
	public async Task<bool> Read_Next()
	{
		int payload_len = ma_recv_buf[1] & 0x7f;  // MASK ビットは消しておく
//		int payload_len = ma_recv_buf[m_recv_pos_unread + 1] & 0x7f;  // MASK ビットは消しておく

		switch (ma_recv_buf[0])
//		switch (ma_recv_buf[m_recv_pos_unread])
		{
		// バイナリフレームの処理
		case EN_WebSocket_Binary:
			break;

		// Pong の処理（現在は CS_Qry_Req_Doc の用途として利用している）
		// Req_Doc を利用する方法は暫定的な方法であるため、手抜きの実装としている。
		case EN_WebSocket_Pong: {
			if (payload_len != 2)
			{ throw new Exception($"!!! WsContext.Read_Next() : 不正な WS パケット受信。pong で payload_len -> {payload_len}"); }

			// cs_idx の取り出し
			uint cs_idx_lo = (uint)(ma_recv_buf[2] ^ ma_recv_buf[6]);
			uint cs_idx_hi = (uint)(ma_recv_buf[3] ^ ma_recv_buf[7]);
//			uint cs_idx_lo = (uint)(ma_recv_buf[m_recv_pos_unread + 2] ^ ma_recv_buf[m_recv_pos_unread + 6]);
//			uint cs_idx_hi = (uint)(ma_recv_buf[m_recv_pos_unread + 3] ^ ma_recv_buf[m_recv_pos_unread + 7]);
//			m_recv_pos_unread += 8;
			uint cs_idx = cs_idx_lo + (cs_idx_hi << 8);

//			Console.WriteLine($"    ws : {m_idx_ws_context} <- Pong");

			if (CmdStream.Take_flags_Req_Doc((ushort)cs_idx) == true)
			{
				// 以下は、Req_Doc を利用する暫定的な方法に対応するため（完成版では不要となる）
				ma_Rea_Doc_send_buf[4] = (byte)cs_idx_lo;
				ma_Rea_Doc_send_buf[5] = (byte)cs_idx_hi;

				lock (m_ns_ws)
				{
					m_ns_ws.Write(ma_Rea_Doc_send_buf, 0, 6);
				}
			}

			this.RecvBuf_Compaction(8);
		} return true;
			
		// クライアントからの ws のクローズ依頼処理（標準ではないはず）
		case EN_WebSocket_Close:
			ms_iLog.WrtLine($"    ws : {m_idx_ws_context} <- Close opcode");
			return false;

		default:
			ms_iLog.Wrt_Warning_Line($"!!! Ws_RecvBuf.Read_Next() : 不正な WS パケット受信。opcode -> 0x{ma_recv_buf[0]:x2}");
			return false;
		}

		// ------------------------------------------------------
		// バイナリフレームの処理
		// まず、payload の長さを確認して、未受信のものがないかを調べる
		int bytes_header_len = 2;
		if (payload_len >= 126)
		{
			if (payload_len == 127)
			{ throw new Exception("!!! Ws_RecvBuf.Read_Next() : ペイロード長が 16bits を超えています。"); }

			bytes_header_len = 4;
			payload_len = (ma_recv_buf[2] << 8) + ma_recv_buf[3];  // ビッグエンディアン

			// -8 は、ヘッダの 4 bytes と マスクキー の 4 bytes を引く必要があるため。
			if (payload_len > Common.EN_bytes_WS_Buf - 8)
			{
				ms_iLog.Wrt_Warning_Line($"!!! Ws_RecvBuf.Read_Next() : 不正な長さの WS パケット受信。ペイロード長 -> {payload_len} bytes");
				return false;
			}
		}

		// ------------------------------------------------------
		// 未受信のものがあれば、残りを受信する
		int bytes_to_consume = bytes_header_len + 4 + payload_len;  // +4 は masking key の分
		for (;;)
		{
			if (m_depo_bytes >= bytes_to_consume) { break; }

//			Console.WriteLine("+++ 追加分の ReadAsync() を実行します。");
			int bytes_recv = await m_ns_ws.ReadAsync(ma_recv_buf, m_depo_bytes, Common.EN_bytes_WS_Buf - m_depo_bytes
																, m_cts_for_ReadAsync_ws_ns.Token);
			m_depo_bytes += bytes_recv;
		}

		// ------------------------------------------------------
		unsafe
		{
			fixed (byte* pTop_recv_buf = ma_recv_buf)
			{
				Unmask_Payload(pTop_recv_buf + bytes_header_len, payload_len);

				// pbuf_unread は、payload の先頭アドレスを示している
//				m_recv_pos_unread = (int)(pbuf_unread + payload_len - pTop_recv_buf);

				// +4 : masking key の４バイト分
				bool ret_val = this.Consume_Payload(pTop_recv_buf + bytes_header_len + 4, payload_len);
				this.RecvBuf_Compaction(bytes_to_consume);

				return ret_val;
			}
		}
	}

	// ------------------------------------------------------------------------------------
	// ThreadProc_ServerClose_by_WS_Context() をコールするのを１回だけにするための措置
	static readonly Tools.Boxed<bool> msb_IsCalled_ServerClose = new (false);

	unsafe bool Consume_Payload(byte* ptr_payload, int bytes_payload)
	{
		switch (*ptr_payload)
		{
		case Common.CMD_CLOSE_SERVER:
			// ThreadProc_ServerClose_by_WS_Context() をコールするのを１回だけにするための措置
			ms_iLog.WrtLine($"    ws : {m_idx_ws_context} <- CMD_CLOSE_SERVER");

			lock (msb_IsCalled_ServerClose)
			{
				if (msb_IsCalled_ServerClose.m_val == true) { return false; }

				msb_IsCalled_ServerClose.m_val = true;
				(new Thread(Server.ThreadProc_ServerClose_by_WS_Context)).Start();
			}
			return false;

		case Common.CMD_UpAllText:
			ms_iLog.WrtLine($"    ws : {m_idx_ws_context} <- CMD_UpAllText");

			CmdStream.IssueCmd_UpAllText(
				*(ushort*)(ptr_payload + 2)		// cs_idx
				, ptr_payload + 6						// pbuf_document
				, *(ushort*)(ptr_payload + 4));	// len_str
			return true;

		case Common.CMD_INSERT:
//			ms_iLog.WrtLine($"    ws : {m_idx_ws_context} <- CMD_INSERT");
			CmdStream.IssueCmd_Payload_AsItIs(m_idx_ws_context, bytes_payload, ptr_payload);
			return true;

		case Common.CMD_REMOVE:
//			ms_iLog.WrtLine($"    ws : {m_idx_ws_context} <- CMD_REMOVE");
			CmdStream.IssueCmd_Payload_AsItIs(m_idx_ws_context, bytes_payload, ptr_payload);
			return true;

		default:
			ms_iLog.Wrt_Warning_Line($"!!! Ws_RecvBuf.Consume_Payload() : 不明な CMD を受信。CMD -> {*ptr_payload:x2}");
			Dump_recv_buf(0);
			return false;
		}
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
	unsafe void RecvBuf_Compaction(int bytes_consumed)
	{
		if (m_depo_bytes == bytes_consumed)
		{
			m_depo_bytes = 0;
			return;
		}

		fixed (byte* pTop_recv_buf = ma_recv_buf)
		{
			m_depo_bytes -= bytes_consumed;

			byte* pdst = pTop_recv_buf;
			byte* psrc = pTop_recv_buf + bytes_consumed;

			for (int i = m_depo_bytes >> 3; i-- > 0; )
			{
				*(ulong*)pdst = *(ulong*)psrc;
				pdst += 8;
				psrc += 8;
			}

			for (int i = m_depo_bytes & 7; i-- > 0; )
			{
				*pdst++ = *psrc++;
			}
		}
	}

	// ------------------------------------------------------------------------------------
	unsafe void Dump_recv_buf(int pos_start, int bytes = 64)
	{
		if (pos_start >= m_depo_bytes)
		{ throw new Exception("!!! Ws_RecvBuf.Dump_recv_buf() : pos_start >= m_depo_bytes"); }

		int bytes_max = m_depo_bytes - pos_start;
		if (bytes > bytes_max) { bytes = bytes_max; }

		fixed (byte* pTop_recv_buf = ma_recv_buf)
		{
			string str = Tools.ByteBuf_toString(pTop_recv_buf + pos_start, bytes);
			Console.WriteLine($"+++ revc_buf の内容：\r\n{str}\r\n");
		}
	}

} // class Ws_RecvBuf

} // partial class WsContext
