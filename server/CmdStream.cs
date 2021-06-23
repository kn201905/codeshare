using System;
using System.Threading;
using System.Collections.Generic;


/////////////////////////////////////////////////////////////////////////////////////////

enum CS_ID : byte
{
	CMD_INSERT = 1,
	CMD_REMOVE = 2,

	// par_additional に、自分自身を含めた接続数が記録される
	CMD_Display_OK = 10,
	CMD_Chg_NumUsrs = 11,

	CS_none = 101,  // 新規コマンドが無かった場合（既に全てを処理してしまっている場合もある）
	CS_ERROR = 102,  // cmd_idx が不一致である場合
	CS_to_Top = 103,

	// -------------------------------------------
	// 128 ～ 255 は、暫定的なコマンド
	CS_Qry_Req_Doc = 130,
//	CMD_Qry_Req_Doc = 130,  // CMD_Qry_Req_Doc は、Ping Pong で代用することにした（実装が簡単なため、、）
	CMD_Req_Doc = 131,
	CS_No_Doc = 132,
	// Up のとき：(byte) cmdID -> 0 -> (ushort) cs_idx -> 文字数（注意：bytes でない）-> 文字列、、、
	// Down のとき：(byte) cmdID -> 0 -> 文字数（注意：bytes でない）-> 文字列、、、
	CMD_UpAllText = 135,
}

/////////////////////////////////////////////////////////////////////////////////////////

// stream の情報格納形式（ヘッダ 10 bytes）
// cs_idx : ushort
// 発行者 ws_context_idx : ushort
// (*) 以降の len : uint（<- WS の payload len を想像する感じで）
// --- ここから上８bytes は、主にサーバのため ---
//
// --- ここから下は、WS ペイロードにそのまま流用できる形式にすることが望ましい ---
// (*) CmdID : byte / par_additional : byte
// (**) データ (pos_inner)

struct CS_Header
{
	public ushort m_cs_idx;
	public ushort m_idx_ws_context_issuer;
	public CS_ID m_cmdID;
	public byte m_par_additional;

	public uint m_cs_pos_payload;  // (*) の pos
	public uint m_bytes_payload;  // (*) の len（２以上）
}

/////////////////////////////////////////////////////////////////////////////////////////

static class CmdStream
{
	// 全てのメンバ変数をロックするために利用する
	static object ms_lock_cmd_stream  = new ();

	const int EN_bytes_CmdStream = 65536;
	static byte[] msa_cs_buf = new byte[EN_bytes_CmdStream];

	static uint ms_cs_pos_unused = 0;  // 参考：ms_cs_pos_unused の場所には、まだデータが入っていない
	static ushort ms_cs_idx_unused = 0;  // ms_cs_idx_unused の値は、未使用の cs_idx となる

	// ------------------------------------------------------------------------------------
	static List<WsContext> ms_List_WsContext = new ();
	public static int Get_WsContexts_Count()
	{
		lock (ms_lock_cmd_stream)
		{
			return ms_List_WsContext.Count;
		}
	}

	// ===================================
	// 以下は暫定処置としてのコード
	// pos_wrtn, cmd_idx_wrtn には、「CMD_Req_Doc」or「CMD_No_Doc」が書き込まれた位置の情報が返される

	// 以下のフラグは、true になるのは Add_WsContext() の中でのみ。false に戻されるのは、Take_flags_Req_Doc() の中でのみ。
	// 目的は、Req_Doc を実行するクライアントを１つにすること。（効率化が目的）
	static bool msb_flags_Req_Doc = true;
	// 以下の値は、Wrt_UpAllText_toCS() に関わらず更新される想定（パケット送信を減らすことが目的）
	static ushort ms_cs_idx_Qry_Req_Doc = 0;

	public static bool Take_flags_Req_Doc(in ushort cs_idx)
	{
		lock (ms_lock_cmd_stream)
		{
			if (cs_idx != ms_cs_idx_Qry_Req_Doc) { return false; }

			if (msb_flags_Req_Doc == true)
			{
				msb_flags_Req_Doc = false;
				return true;
			}
			else
			{
				return false;
			}
		}
	}

	// ===================================
	// 以下のフラグは、true になるのは Add_WsContext() の中でのみ。false に戻されるのは、Wrt_UpAllText_toCS() の中でのみ。
	// Wrt_UpAllText_toCS() の中で、フラグを false に戻すと同時に CMD_UpAllText を CS に書き込む。
	// true になっているということは、まだ CMD_UpAllText が書き込まれていない、ということを示す。
	static bool msb_is_waiting_UpAllText = false;

	// ----------------------------
	// CS_inner に記録されるのは、document の文字列のみ

	public static unsafe void IssueCmd_UpAllText(in ushort cs_idx, byte* pbuf_str_document, in uint len_str)
	{
		lock (ms_lock_cmd_stream)
		{
			if (cs_idx != ms_cs_idx_Qry_Req_Doc) { return; }

			// 既に、他のクライアントによって CMD_UpAllText の処理が終わっていることがある。
			if (msb_is_waiting_UpAllText == false) { return; }

			fixed (byte* pTop_cs_buf = msa_cs_buf)
			{
				uint bytes_str = len_str << 1;
				byte* pbuf_pos_inner = pTop_cs_buf + NoLc_Alloc_CS_Buf_with_CS_ID(0, bytes_str + 4, CS_ID.CMD_UpAllText);

				*(ushort*)pbuf_pos_inner = (ushort)len_str;
				pbuf_pos_inner += 2;

				for (uint i = bytes_str >> 3; i-- > 0; )
				{
					*(ulong*)pbuf_pos_inner = *(ulong*)pbuf_str_document;
					pbuf_pos_inner += 8;
					pbuf_str_document += 8;
				}

				for (uint i = bytes_str & 7; i-- > 0; )
				{
					*pbuf_pos_inner++ = *pbuf_str_document++;
				}
			}
			msb_is_waiting_UpAllText = false;

			foreach (WsContext ws_context in ms_List_WsContext)
			{
				ws_context.Up_semph_WS_Cmd();
			}
		}
	}

	// ------------------------------------------------------------------------------------
	// Req_Doc を利用する方法は暫定的な方法であるため、手抜きの実装としているため、
	// Add_WsContext() がコールされた場合、必ず CS_Qry_Req_Doc が発行される形とする。
	// msb_flags_Req_Doc をとったクライアントが、ハングアップしても回復できるようにするための措置。

	public static void Add_WsContext(WsContext ws_context_to_add, out uint cs_pos_wrtn, out ushort cs_idx_wrtn)
	{
		lock (ms_lock_cmd_stream)
		{
			bool b_do_issue_Qry_Req_Doc = false;
			foreach (WsContext ws_context in ms_List_WsContext)
			{
				if (ws_context.bIs_Code_Displayed_on_client() == true)
				{
					b_do_issue_Qry_Req_Doc = true;
					break;
				}
			}

			// ws_context_to_add 以外の ws_context に向けて、Chg_NumUsrs を発行する
			NoLc_Alloc_CS_Buf_with_CS_ID(0, 2, CS_ID.CMD_Chg_NumUsrs, (byte)(ms_List_WsContext.Count + 1));

			if (b_do_issue_Qry_Req_Doc == true)
			{
				ms_cs_idx_Qry_Req_Doc = ms_cs_idx_unused;  // CS_Qry_Req_Doc に対応する cs_idx となる。

				NoLc_Alloc_CS_Buf_with_CS_ID(0, 2, CS_ID.CS_Qry_Req_Doc);
				msb_is_waiting_UpAllText = true;
				msb_flags_Req_Doc = true;  // このフラグの目的は、Req_Doc を実行するクライアントを１つにすること
			}
			else
			{
				NoLc_Alloc_CS_Buf_with_CS_ID(0, 2, CS_ID.CS_No_Doc);
			}

			foreach (WsContext ws_context in ms_List_WsContext)
			{
				if (ws_context.bIs_Code_Displayed_on_client() == true) { ws_context.Up_semph_WS_Cmd(); }
			}

			// ----------------------------------------
			// ws_context_to_add には、「CMD_Req_Doc」or「CMD_No_Doc」を書き込んだ位置の情報を返す
			cs_pos_wrtn = ms_cs_pos_unused - 10;
			cs_idx_wrtn = (ushort)(ms_cs_idx_unused - 1);

			ms_List_WsContext.Add(ws_context_to_add);
		}
	}

	// ------------------------------------------------------------------------------------
	// 戻り値： ms_List_WsContext に ws_context_to_remove が存在しないなどの理由で Remove() ができなかった場合、
	// false が返される。false の場合、特に何かが出来るわけではないが、エラー顕在化の処理が望まれる。

	public static bool Remove_WsContext(WsContext ws_context_to_remove)
	{
		lock (ms_lock_cmd_stream)
		{
			bool ret_val = ms_List_WsContext.Remove(ws_context_to_remove);

			NoLc_Alloc_CS_Buf_with_CS_ID(0, 2, CS_ID.CMD_Chg_NumUsrs, (byte)(ms_List_WsContext.Count));
			foreach (WsContext ws_context in ms_List_WsContext)
			{
				if (ws_context.bIs_Code_Displayed_on_client() == true) { ws_context.Up_semph_WS_Cmd(); }
			}

			return ret_val;
		}
	}

	// ------------------------------------------------------------------------------------
	// io_pos, io_cmd_idx には、次への更新された値が設定される

	public unsafe static void GetNextCmd(ref uint io_cs_pos, ref ushort io_cs_idx, ref CS_Header o_cs_header)
	{
		lock (ms_lock_cmd_stream)
		{
			if (ms_cs_pos_unused == io_cs_pos)
			{
				o_cs_header.m_cmdID = CS_ID.CS_none;
				return;
			}

			fixed (byte* pTop_cs_buf = msa_cs_buf)
			{
				byte* pbuf_cs = pTop_cs_buf + io_cs_pos;
				// エラー顕在化のためのチェック
				if (*(ushort*)pbuf_cs != io_cs_idx++)
				{
					o_cs_header.m_cmdID = CS_ID.CS_ERROR;
					return;
				}
				
				// -----------------------------------------------
				// CS からデータを取得する
				CS_ID cmdID = (CS_ID)(*(pbuf_cs + 8));
				if (cmdID == CS_ID.CS_to_Top)
				{
					io_cs_idx++;
					pbuf_cs = pTop_cs_buf;
					cmdID = (CS_ID)(*(pbuf_cs + 8));
				}

				o_cs_header.m_cs_idx = *(ushort*)pbuf_cs;
				o_cs_header.m_idx_ws_context_issuer = *(ushort*)(pbuf_cs + 2);
				uint bytes_payload
					= o_cs_header.m_bytes_payload = *(uint*)(pbuf_cs + 4);

				o_cs_header.m_cmdID = cmdID;
				o_cs_header.m_par_additional = *(pbuf_cs + 9);

				uint cs_pos_payload
					= o_cs_header.m_cs_pos_payload = (uint)(pbuf_cs - pTop_cs_buf) + 8;

				io_cs_pos = cs_pos_payload + bytes_payload;
			}
		}
	}

	// ------------------------------------------------------------------------------------
	// 戻り値は、書き込んだヘッダに対するデータ書き込み位置（(**) の位置。cs_pos_inner）
	// bytes には、cmdID 以降の長さを指定することに注意（基本的に WS payload len と同じになり、２以上の数となるはず）

	static unsafe uint NoLc_Alloc_CS_Buf_with_CS_ID(
			in ushort idx_ws_context_issuer, in uint bytes, in CS_ID cmdID, in byte par_additional = 0)
	{
		fixed (byte* pTop_buf = msa_cs_buf)
		{
			byte* pbuf_unused = pTop_buf + ms_cs_pos_unused;

			// まず、バッファに bytes の長さが確保できるかどうかのチェックをする
			ms_cs_pos_unused += 8 + bytes;
			if (ms_cs_pos_unused > EN_bytes_CmdStream - 10)  // -10 : CS_to_Top を書き込める最小の bytes
			{
				*(ushort*)pbuf_unused = ms_cs_idx_unused++;
//				*(ushort*)(pbuf_unused + 2) = 0;  // CS_to_Top に issuer 情報は必要ない
				*(uint*)(pbuf_unused + 4) = 2;  // len 情報も省略できるはずだけど、、、
				*(pbuf_unused + 8) = (byte)CS_ID.CS_to_Top;

				pbuf_unused = pTop_buf;
				ms_cs_pos_unused = 8 + bytes;
			}

			*(ushort*)pbuf_unused = ms_cs_idx_unused++;
			*(ushort*)(pbuf_unused + 2) = idx_ws_context_issuer;
			*(uint*)(pbuf_unused + 4) = bytes;

			*(pbuf_unused + 8) = (byte)cmdID;
			*(pbuf_unused + 9) = par_additional;

			return ms_cs_pos_unused - bytes + 2;  // +2 : cs_pos_inner に補正するための２bytes
		}
	}

	// ------------------------------------------------------------------------------------
	// bytes は payload bytes をイメージすること
	// 戻り値の pos は、CS_ID の書き込み位置となっていることに注意

	static unsafe uint NoLc_Alloc_CS_Buf(in ushort idx_ws_context_issuer, in uint bytes)
	{
		fixed (byte* pTop_buf = msa_cs_buf)
		{
			byte* pbuf_unused = pTop_buf + ms_cs_pos_unused;

			// まず、バッファに bytes の長さが確保できるかどうかのチェックをする
			ms_cs_pos_unused += 8 + bytes;
			if (ms_cs_pos_unused > EN_bytes_CmdStream - 10)  // -10 : CS_to_Top を書き込める最小の bytes
			{
				*(ushort*)pbuf_unused = ms_cs_idx_unused++;
//				*(ushort*)(pbuf_unused + 2) = 0;  // CS_to_Top に issuer 情報は必要ない
				*(uint*)(pbuf_unused + 4) = 2;  // len 情報も省略できるはずだけど、、、
				*(pbuf_unused + 8) = (byte)CS_ID.CS_to_Top;

				pbuf_unused = pTop_buf;
				ms_cs_pos_unused = 8 + bytes;
			}

			*(ushort*)pbuf_unused = ms_cs_idx_unused++;
			*(ushort*)(pbuf_unused + 2) = idx_ws_context_issuer;
			*(uint*)(pbuf_unused + 4) = bytes;

			return ms_cs_pos_unused - bytes;
		}
	}

	// ------------------------------------------------------------------------------------
	static unsafe void NoLc_Alloc_CS_Buf_And_Copy_Payload
			(in ushort idx_ws_context_issuer, uint bytes_payload, byte* ptr_payload)
	{
		fixed (byte* pTop_buf = msa_cs_buf)
		{
			byte* pbuf_unused = pTop_buf + ms_cs_pos_unused;

			// まず、バッファに bytes の長さが確保できるかどうかのチェックをする
			ms_cs_pos_unused += 8 + bytes_payload;
			if (ms_cs_pos_unused > EN_bytes_CmdStream - 10)  // -10 : CS_to_Top を書き込める最小の bytes
			{
				*(ushort*)pbuf_unused = ms_cs_idx_unused++;
//				*(ushort*)(pbuf_unused + 2) = 0;  // CS_to_Top に issuer 情報は必要ない
				*(uint*)(pbuf_unused + 4) = 2;  // len 情報も省略できるはずだけど、、、
				*(pbuf_unused + 8) = (byte)CS_ID.CS_to_Top;

				pbuf_unused = pTop_buf;
				ms_cs_pos_unused = 8 + bytes_payload;
			}

			*(ushort*)pbuf_unused = ms_cs_idx_unused++;
			*(ushort*)(pbuf_unused + 2) = idx_ws_context_issuer;
			*(uint*)(pbuf_unused + 4) = bytes_payload;

			pbuf_unused += 8;
			for (uint i = bytes_payload >> 3; i-- > 0; )
			{
				*(ulong*)pbuf_unused = *(ulong*)ptr_payload;
				pbuf_unused += 8;
				ptr_payload += 8;
			}

			for (uint i = bytes_payload & 7; i-- > 0; )
			{
				*pbuf_unused++ = *ptr_payload++;
			}
		}
	}

	// ------------------------------------------------------------------------------------
	// ストリームバッファ内容のコピーは、lock する必要がないはず

	public static unsafe void NoLc_Copy_CS_buf_to(in byte[] dst_ary_buf, in int dst_pos, in uint cs_pos, uint bytes)
	{
		fixed (byte* pTop_buf_dst = dst_ary_buf)
		fixed (byte* pTop_cs_buf = msa_cs_buf)
		{
			byte* pdst = pTop_buf_dst + dst_pos;
			byte* pbuf_cs = pTop_cs_buf + cs_pos;

			for (; bytes >= 8; bytes -= 8)
			{
				*(ulong*)pdst = *(ulong*)pbuf_cs;
				pdst += 8;
				pbuf_cs += 8;
			}

			for (; bytes-- > 0; )
			{
				*pdst++ = *pbuf_cs++;
			}
		}
	}

	// ------------------------------------------------------------------------------------
	public static unsafe void IssueCmd_Payload_AsItIs(ushort ws_context_issuer, int bytes_payload, byte* ptr_payload)
	{
		lock (ms_lock_cmd_stream)
		{
			NoLc_Alloc_CS_Buf_And_Copy_Payload(ws_context_issuer, (uint)bytes_payload, ptr_payload);

			foreach (WsContext ws_context in ms_List_WsContext)
			{
				ws_context.Up_semph_WS_Cmd();
			}
		}
	}
}
