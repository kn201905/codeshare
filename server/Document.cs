using System;
using System.Collections.Generic;
using System.Diagnostics;  // Conditinal を利用するため

// 128文字／行 を基準として考える（１文字 = ushort）
// クライアント js の制限で、１行あたりの文字数の最大値は 255文字
// 1000行／Document を基準とする

// 以上の考え方で、1 Document ＝ 256 KBytes が基準

public class Document
{
	// 現在は、１チャンクあたりの最小の文字数は６４文字としている
	const int EN_min_chrs_line = 64;
	const int EN_min_chrs_allocd = EN_min_chrs_line + 1;  // 1 は、先頭の文字数バッファ
	// チャンクを拡張する場合、必要文字に加えて以下の文字数を余分にとる
	const int EN_chrs_rsvd_on_expand = 32;
	const int EN_chrs_diff_min_rsvd = EN_min_chrs_line - EN_chrs_rsvd_on_expand;

	string m_doc_name = null;

	// ushort[] の先頭は、使用文字数を表す
	readonly List<ushort[]> m_lines = new ();
//	byte m_doc_idx = 0;

	// ------------------------------------------------------------------------------------
	public Document(string doc_name)
	{
		m_doc_name = doc_name;

		ushort[] line = new ushort[EN_min_chrs_allocd];
		line[0] = 0;
		m_lines.Add(line);
	}

	// ------------------------------------------------------------------------------------
	// ユニットテスト用
	public unsafe Document(string doc_name, string[] ary_str)
	{
		m_doc_name = doc_name;

		foreach(string str in ary_str)
		{
			int chrs_line = str.Length;
			if (chrs_line > 255)
			{ throw new Exception("!!! Document コンストラクタ：１行の文字数が 255文字を超えていました。"); }

			int chrs_allocd = (chrs_line < EN_chrs_diff_min_rsvd)? EN_min_chrs_line : chrs_line + EN_chrs_rsvd_on_expand;

			ushort[] new_line = new ushort[chrs_allocd];
			fixed (ushort* pTop_line = new_line)
			fixed (char* pTop_str = str)
			{
				*pTop_line = (ushort)chrs_line;
//				Console.WriteLine($"コンストラクタ : chrs_line -> {chrs_line}");

				ushort* pdst = pTop_line + 1;
				ushort* psrc = (ushort*)pTop_str;

				while (chrs_line-- > 0) { *pdst++ = *psrc++; }
			}
			m_lines.Add(new_line);
		}
	}

	// ------------------------------------------------------------------------------------
	// 0x0d0a または 0x0a 改行で格納されている文字列から、Document を生成する

//	[System.Runtime.CompilerServices.SkipLocalsInit]
	public unsafe void From_Utf16(byte* pbuf_by_byte, int bytes_utf16_str)
	{
		if ((bytes_utf16_str & 1) != 0)
		{ throw new Exception("!!! Document.Load() : bytes_utf16_str の値が奇数となっています。"); }

		m_lines.Clear();
		if (bytes_utf16_str == 0)
		{
			ushort[] line = new ushort[EN_min_chrs_allocd];
			line[0] = 0;
			m_lines.Add(line);
			return;
		}

		// ----------------------
		ushort* pTmnt_buf = (ushort*)(pbuf_by_byte + bytes_utf16_str);
		ushort* pbuf = (ushort*)pbuf_by_byte;

		if (*pbuf == 0x000a)
		{
			ushort[] line = new ushort[EN_min_chrs_allocd];
			line[0] = 0;
			m_lines.Add(line);
				
			if (++pbuf == pTmnt_buf) { return; }
		}

		// ----------------------
		for (;;)
		{
			// ここでは pbuf < pTmnt_buf
			ushort* psrc = pbuf;
			ushort* ptmnt_line;
			for (;;)
			{
				if (*pbuf == 0x000a)
				{
					ptmnt_line = (*(pbuf - 1) == 0x000d)? pbuf - 1 : pbuf;
					break;
				}

				if (++pbuf == pTmnt_buf)
				{
					ptmnt_line = pbuf;
					break;
				}
			}  // for (;;)

			int chrs_line = (int)(ptmnt_line - psrc);
			ushort[] new_ary_line = new ushort[
					(chrs_line < EN_chrs_diff_min_rsvd)? EN_min_chrs_line : chrs_line + EN_chrs_rsvd_on_expand];

			fixed (ushort* pTop_line = new_ary_line)
			{
				*pTop_line = (ushort)chrs_line;
				ushort* pdst = pTop_line + 1;
				while (psrc < ptmnt_line) { *pdst++ = *psrc++; }
			}
			m_lines.Add(new_ary_line);

			if (++pbuf >= pTmnt_buf) { return; }
		}
	}

	// ------------------------------------------------------------------------------------
	// 0x0d0a 改行文字列へ変換する（最終行にも 0x0d0a を挿入する）

	public unsafe void To_Utf16(ushort* pbuf, int bytes_buf)
	{
		int rem_chrs_buf = bytes_buf >> 1;

		foreach (ushort[] ary_line in m_lines)
		{
			fixed (ushort* pTop_line = ary_line)
			{
				ushort chrs_line = ary_line[0];

				rem_chrs_buf -= chrs_line + 2;
				if (rem_chrs_buf < 0)
				{ throw new Exception("!!! Document.To_Utf16() : バッファオーバーフロー"); }

				ushort* psrc = pTop_line + 1;
				while (chrs_line-- > 0) { *pbuf++ = *psrc++; }

				*(uint*)pbuf = 0x000a000d;
				pbuf += 2;
			}
		}
	}

	// ------------------------------------------------------------------------------------
	// 0x0d0a 改行文字列へ変換する（最終行にも 0x0d0a を挿入する）

	public unsafe override string ToString()
	{
		// まず必要な文字数をカウントする
		int chrs_all = 0;
		foreach (ushort[] line in m_lines) { chrs_all += line[0] + 2; }

		return String.Create<List<ushort[]>>(chrs_all, m_lines, (chars, lines) => {
			fixed (char* pTop_chars = chars)
			{
				ushort* pdst = (ushort*)pTop_chars;
				foreach (ushort[] line in lines)
				{
					fixed (ushort* pTop_line = line)
					{
						ushort* psrc = pTop_line + 1;
						for (int i = *pTop_line; i-- > 0; ) { *pdst++ = *psrc++; }
					}
					*(uint*)pdst = 0x000a000d;
					pdst += 2;
				}
			}
		});
	}

	// ------------------------------------------------------------------------------------
	// 戻り値： エラーがあり、情報を統合できないとき false が返される

	[System.Runtime.CompilerServices.SkipLocalsInit]
	public unsafe bool CMD_Insert(byte* ptr_payload, int bytes_payload)
	{
		// -----------------------------------------------------
		// R と C の確認
		int row = *(ushort*)(ptr_payload + 2);
		int column = *(ushort*)(ptr_payload + 4);
		int pcs_lines = *(ushort*)(ptr_payload + 6);

		if (row >= m_lines.Count)
		{
			CmdStream.IssueCmd_Msg(CS_Par.Par_Critical, "Document.CMD_Insert() : row >= m_lines.Count");
			return false;
		}
		
		ushort[] ary_tgt_line = m_lines[row];
		int old_chrs_tgt_line = ary_tgt_line[0];
		if (old_chrs_tgt_line < column)
		{
			CmdStream.IssueCmd_Msg(CS_Par.Par_Critical, "Document.CMD_Insert() : old_chrs_tgt_line < column");
			return false;
		}

		// -----------------------------------------------------
		// 挿入後の文字数の確認
		byte* p_chrs = ptr_payload + 8;
		int chrs_to_add_tgt_line = *p_chrs;
		ushort* p_strs = (ushort*)(p_chrs + ((pcs_lines + 1) & 0xfffe));

		int new_chrs_tgt_line = (pcs_lines == 1) ? old_chrs_tgt_line + chrs_to_add_tgt_line : column + chrs_to_add_tgt_line;
		if (new_chrs_tgt_line > 255)
		{
			CmdStream.IssueCmd_Msg(CS_Par.Par_Critical, "Document.CMD_Insert() : 挿入行の文字数が 255文字を超えました。(1)");
			return false;
		}

		// tgt_line 行の拡張が必要である場合、ここで new_tgt_line を作成しておく
		ushort[] new_ary_tgt_line = null;
		if (new_chrs_tgt_line > ary_tgt_line.Length - 1)  // ary_tgt_line.Length >= EN_min_chrs_allocd であることに注意
		{
			int chrs_allocd = new_chrs_tgt_line + EN_chrs_rsvd_on_expand;
			if (chrs_allocd > 256) { chrs_allocd = 256; }

			new_ary_tgt_line = new ushort[chrs_allocd];

			new_ary_tgt_line[0] = (ushort)new_chrs_tgt_line;  // 文字数の設定
			m_lines[row] = new_ary_tgt_line;
		}

		// --------------------------------------------------
		if (pcs_lines == 1)
		{
			if (new_ary_tgt_line != null)
			{
				// tgt_line 行の拡張が必要である場合
				fixed (ushort* pTop_dst_line = new_ary_tgt_line)
				fixed (ushort* pTop_tgt_line = ary_tgt_line)
				{
					ushort* pdst = pTop_dst_line + 1;
					ushort* psrc = pTop_tgt_line + 1;
					for (int i = column; i-- > 0; ) { *pdst++ = *psrc++; }
					for (int i = chrs_to_add_tgt_line; i-- > 0; ) { *pdst++ = *p_strs++; }
					for (int i = old_chrs_tgt_line - column; i-- > 0; )  { *pdst++ = *psrc++; }
				}
				return true;
			}

			// tgt_line 行の拡張が不必要である場合
			fixed (ushort* pTop_tgt_line = ary_tgt_line)
			{
				*pTop_tgt_line = (ushort)new_chrs_tgt_line;  // 文字数の更新

				// １文字追加の操作は最適化しておく
				if (chrs_to_add_tgt_line == 1)
				{
					if (column == old_chrs_tgt_line)  // 行末に追加
					{
						*(pTop_tgt_line + column + 1) = *p_strs;
						return true;
					}
					else
					{
						ushort* pdst = pTop_tgt_line + new_chrs_tgt_line;
						for (int i = old_chrs_tgt_line - column; i-- > 0; ) { *pdst = *(pdst - 1); pdst--; }
						*(pTop_tgt_line + column + 1) = *p_strs;
						return true;
					}
				}

				ushort* pdst_2 = pTop_tgt_line + new_chrs_tgt_line;
				ushort* psrc_2 = pTop_tgt_line + old_chrs_tgt_line;

				for (int i = old_chrs_tgt_line - column; i-- > 0; ) { *pdst_2-- = *psrc_2--; }
				for (int i = chrs_to_add_tgt_line; i-- > 0; ) { *++psrc_2 = *p_strs++; }
			}
			return true;
		}

		// --------------------------------------------------
		int idx_line_ins = row + 1;
		bool b_use_add = (m_lines.Count == idx_line_ins);

		if (pcs_lines == 2 && bytes_payload == 10)  // 改行処理は最適化しておく（オーバーフロー判定など不要）
		{
			if (column == old_chrs_tgt_line)  // 行末改行
			{
				ushort[] new_line = new ushort[EN_min_chrs_allocd];
				new_line[0] = 0;

				if (b_use_add == true) { m_lines.Add(new_line); }
				else { m_lines.Insert(idx_line_ins, new_line); }
				return true;
			}
			else if (column == 0)  // 行先頭改行
			{
				ushort[] new_line = new ushort[EN_min_chrs_allocd];
				new_line[0] = 0;

				m_lines.Insert(row, new_line);
				return true;
			}
			else  // 行途中改行
			{
				ary_tgt_line[0] = (ushort)column;

				int chrs_new_line = old_chrs_tgt_line - column;
				int chrs_allocd
					= (chrs_new_line < EN_chrs_diff_min_rsvd)? EN_min_chrs_line : chrs_new_line + EN_chrs_rsvd_on_expand;
				ushort[] new_line = new ushort[chrs_allocd];

				fixed (ushort* pTop_new_line = new_line)
				fixed (ushort* pTop_tgt_line = ary_tgt_line)
				{
					*pTop_new_line = (ushort)chrs_new_line;

					ushort* pdst = pTop_new_line + 1;
					ushort* psrc = pTop_tgt_line + column + 1;
					while (chrs_new_line-- > 0) { *pdst++ = *psrc++; }					
				}

				if (b_use_add == true) { m_lines.Add(new_line); }
				else { m_lines.Insert(idx_line_ins, new_line); }
				return true;
			}
		}

		// pcs_lines >= 2 であるとき
		// 先頭行と最終行の文字数のチェックが必要となる
		int chrs_to_move_tgt_line = old_chrs_tgt_line - column;
		int chrs_to_add_last_line = *(p_chrs + pcs_lines - 1);
		int chrs_last_line = chrs_to_move_tgt_line + chrs_to_add_last_line;
		if (chrs_last_line > 255)
		{
			CmdStream.IssueCmd_Msg(CS_Par.Par_Critical, "Document.CMD_Insert() : 挿入行の文字数が 255文字を超えました。(2)");
			return false;
		}

		// 最終行へ移動させるべき文字列が破壊されないように、先頭行の処理は最後に行う
		ushort* p_strs_add_tgt_line = p_strs;
		p_strs += chrs_to_add_tgt_line;

		// 先頭行、最終行以外の処理
		for (int i = pcs_lines - 2; i-- > 0; )
		{
			// chrs_allocd > 255 となることはない。事前チェックがされているため。
			int chrs_new_line = *++p_chrs;

			int chrs_allocd;
			if (chrs_new_line < EN_chrs_diff_min_rsvd) { chrs_allocd = EN_min_chrs_line; }
			else
			{
				chrs_allocd = chrs_new_line + EN_chrs_rsvd_on_expand;
				if (chrs_allocd > 256) { chrs_allocd = 256; }
			}

			ushort[] new_line = new ushort[chrs_allocd];
			fixed (ushort* pTop_new_line = new_line)
			{
				*pTop_new_line = (ushort)chrs_new_line;  // 文字数の設定

				ushort* pdst = pTop_new_line + 1;
				while (chrs_new_line-- > 0) { *pdst++ = *p_strs++; }
			}
			if (b_use_add == true) { m_lines.Add(new_line); }
			else { m_lines.Insert(idx_line_ins++, new_line); }
		}

		// 最終行のバッファ確保
		int chrs_allocd_last_line;
		if (chrs_last_line < EN_chrs_diff_min_rsvd) { chrs_allocd_last_line = EN_min_chrs_line; }
		else
		{
			chrs_allocd_last_line = chrs_last_line + EN_chrs_rsvd_on_expand;
			if (chrs_allocd_last_line > 256) { chrs_allocd_last_line = 256; }
		}

		ushort[] last_line = new ushort[chrs_allocd_last_line];
		if (b_use_add == true) { m_lines.Add(last_line); }
		else { m_lines.Insert(idx_line_ins, last_line); }

		fixed(ushort* pTop_tgt_line = ary_tgt_line)
		fixed(ushort* pTop_last_line = last_line)
		{
			// 最終行の生成
			*pTop_last_line = (ushort)chrs_last_line;  // 文字数の設定

			ushort* p_last_line = pTop_last_line + 1;
			ushort* p_tgt_line = pTop_tgt_line + column + 1;
			for (int i = chrs_to_add_last_line; i-- > 0; ) { *p_last_line++ = *p_strs++; }
			for (int i = chrs_to_move_tgt_line; i-- > 0; ) { *p_last_line++ = *p_tgt_line++; }

			// 先頭行の処理
			if (new_ary_tgt_line != null)
			{
				// 先頭行を拡張した場合
				fixed (ushort* pTop_new_tgt = new_ary_tgt_line)
				fixed (ushort* pTop_old_tgt = ary_tgt_line)
				{
					ushort* p_new_tgt = pTop_new_tgt + 1;
					ushort* p_old_tgt = pTop_old_tgt + 1;
					for (int i = column; i-- > 0; ) { *p_new_tgt++ = *p_old_tgt++; }
					for (int i = chrs_to_add_tgt_line; i-- > 0; ) { *p_new_tgt++ = *p_strs_add_tgt_line++; }
					return true;
				}
			}

			// 先頭行の拡張が不要であった場合
			*pTop_tgt_line = (ushort)new_chrs_tgt_line;  // 文字数の設定

			p_tgt_line = pTop_tgt_line + column + 1;  // 1: 先頭の文字数情報分
			for (int i = chrs_to_add_tgt_line; i-- > 0; ) { *p_tgt_line++ = *p_strs_add_tgt_line++; }
			return true;
		}
	}

	// ------------------------------------------------------------------------------------
	// 戻り値： エラーがあり、情報を統合できないとき false が返される
	public unsafe bool CMD_Remove(byte* ptr_payload, int bytes_payload)
	{
		// -----------------------------------------------------
		// start R と C の確認
		int start_row = *(ushort*)(ptr_payload + 2);
		int start_column = *(ushort*)(ptr_payload + 4);
		int pcs_lines = *(ushort*)(ptr_payload + 6);

		if (start_row >= m_lines.Count)
		{
			CmdStream.IssueCmd_Msg(CS_Par.Par_Critical, "Document.CMD_Remove() : start_row >= m_lines.Count");
			return false;
		}
		
		ushort[] ary_tgt_line = m_lines[start_row];
		int old_chrs_tgt_line = ary_tgt_line[0];
		if (old_chrs_tgt_line < start_column)
		{
			CmdStream.IssueCmd_Msg(CS_Par.Par_Critical, "Document.CMD_Remove() : old_chrs_tgt_line < start_column");
			return false;
		}

		// -----------------------------------------------------
		byte* p_chrs = ptr_payload + 8;
		ushort* p_strs = (ushort*)(p_chrs + ((pcs_lines + 1) & 0xfffe));

		if (pcs_lines == 1)
		{
			int chrs_to_remove = *p_chrs;
			int chrs_to_move = old_chrs_tgt_line - start_column - chrs_to_remove;

			if (chrs_to_move < 0)
			{
				CmdStream.IssueCmd_Msg(CS_Par.Par_Critical, "Document.CMD_Remove() : pcs_lines == 1 && chrs_to_move < 0");
				return false;
			}

			ary_tgt_line[0] = (ushort)(old_chrs_tgt_line - chrs_to_remove);  // 文字数設定
			if (chrs_to_move == 0) { return true; }

			fixed(ushort* pTop_tgt_line = ary_tgt_line)
			{
				ushort* pdst = pTop_tgt_line + start_column + 1;
				ushort* psrc = pdst + chrs_to_remove;
				while (chrs_to_move-- > 0) { *pdst++ = *psrc++; }
			}
			return true;
		}

		// -----------------------------------------------------
		// end R と C の確認
		int end_row = start_row + pcs_lines - 1;
		if (end_row >= m_lines.Count)
		{
			CmdStream.IssueCmd_Msg(CS_Par.Par_Critical, "Document.CMD_Remove() : end_row >= m_lines.Count");
			return false;
		}

		ushort[] ary_end_line = m_lines[end_row];
		int chrs_to_remove_end_line = *(p_chrs + pcs_lines - 1);
		int old_chrs_end_line = ary_end_line[0];
		int chrs_to_move_end_column = old_chrs_end_line - chrs_to_remove_end_line;
		if (chrs_to_move_end_column < 0)
		{
			CmdStream.IssueCmd_Msg(CS_Par.Par_Critical, "Document.CMD_Remove() : chrs_to_move_end_column < 0");
			return false;
		}

		// -----------------------------------------------------
		// 最終行を、ターゲット行に利用できる場合の最適化
		if (start_column == 0 && chrs_to_remove_end_line == 0)
		{
			m_lines[start_row] = m_lines[end_row];

			int _idx_rmv = start_row + 1;
			for (int i = pcs_lines - 1; i-- > 0; ) { m_lines.RemoveAt(_idx_rmv); }
			return true;
		}

		// -----------------------------------------------------
		ary_tgt_line[0] = (ushort)(start_column + chrs_to_move_end_column);  // 文字数設定
		if (pcs_lines == 2)
		{
			if (chrs_to_move_end_column == 0)
			{
				m_lines.RemoveAt(end_row);
				return true;
			}

			fixed (ushort* pTop_tgt_line = ary_tgt_line)
			fixed (ushort* pTop_end_line = ary_end_line)
			{
				ushort* pdst = pTop_tgt_line + start_column + 1;
				ushort* psrc = pTop_end_line + chrs_to_remove_end_line + 1;
				while (chrs_to_move_end_column-- > 0) { *pdst++ = *psrc++; }
			}
			m_lines.RemoveAt(end_row);
			return true;
		}

		// -----------------------------------------------------
		// pcs_lines > 2 のときの処理
		int idx_rmv = start_row + 1;
		if (chrs_to_move_end_column == 0)
		{
			for (int i = pcs_lines - 1; i-- > 0; ) { m_lines.RemoveAt(idx_rmv); }
			return true;
		}

		fixed (ushort* pTop_tgt_line = ary_tgt_line)
		fixed (ushort* pTop_end_line = ary_end_line)
		{
			ushort* pdst = pTop_tgt_line + start_column + 1;
			ushort* psrc = pTop_end_line + chrs_to_remove_end_line + 1;
			while (chrs_to_move_end_column-- > 0) { *pdst++ = *psrc++; }
		}

		for (int i = pcs_lines - 1; i-- > 0; ) { m_lines.RemoveAt(idx_rmv); }
		return true;
	}

}  // class Document

#if false
class DocHeap
{
	// 各 CHUNK の先頭は、利用している場合、文字数とする。未使用 CHUNK の先頭は 0xffff。
	// 最大でも 65535 以下とする。CHUNK_idx 0xffff は、未割り当てを示すため。
	const int EN_pcs_CHUNK = 1000;
	const int EN_len_CHUNK = 128;  // これは固定（7 bit シフトで idx を算出するようにしているため）

	const int EN_pcs_on_heap = EN_len_CHUNK * EN_pcs_CHUNK;
	ushort[] ma_heap = new ushort[EN_pcs_on_heap];

	// CHUNK_idx は 0 <= CHUNK_idx < EN_pcs_UNIT。CHUNK_idx 0xffff は、未割り当てを示す。
	ushort[] ma_CHUNK_idx_for_LINE = new ushort[EN_pcs_CHUNK];

	// ------------------------------------------------------------------------------------
	public DocHeap()
	{
		unsafe
		{
			// 全チャンクを未使用にマーク
			fixed (ushort* pTop_heap = ma_heap)
			{
				ushort* pheap = pTop_heap;
				for (int i = EN_pcs_CHUNK; i-- > 0; )
				{
					*pheap = 0xffff;  // 0xffff は、未使用CHUNK であることを表す
					pheap += EN_len_CHUNK;
				}
			}

			// CHUNK_idx を未使用にマーク
			fixed (ushort* pTop_UNIT_idx = ma_CHUNK_idx_for_LINE)
			{
				ushort* pbuf = pTop_UNIT_idx;
				for (int i = EN_pcs_CHUNK; i-- > 0; )
				{ *pbuf++ = 0xffff; }
			}
		}
	}

	// ------------------------------------------------------------------------------------
	// 現時点では、コンパクションは未サポート
	// 戻り値の Heap_idx は、実際に書き込める CHUNK の最初の idx（文字数を格納している idx ではない）

	public int Get_Heap_idx_for_insert(in uint line, in uint pcs_add)  // line >= 0 / pcs_add >= 1
	{
		// エラー顕在化
		if (line >= EN_pcs_CHUNK)
		{ throw new Exception("!!! DocHeap.Get_Heap_idx_for_LINE() : line >= EN_pcs_CHUNK"); }

		lock (this)
		{
			ushort cur_CHUNK_idx = ma_CHUNK_idx_for_LINE[line];

			if (cur_CHUNK_idx == 0xffff)
			{
				// エラー顕在化
				if (pcs_add > 255) 
				{ throw new Exception("!!! DocHeap.Get_Heap_idx_for_insert() 新規割当 : pcs_add > 255"); }

				// 新規に CHUNK を割り当てる
				int ret_heap_idx = (pcs_add <= 127)
					? Get_free_CHUNK_for_1CHUNK((ushort)pcs_add) : Get_free_CHUNK_for_2CHUNK((ushort)pcs_add);

				ma_CHUNK_idx_for_LINE[line] = (ushort)(ret_heap_idx >> 7);
				return ret_heap_idx + 1;
			}

			// 既に CHUNK が割り当てられてる行に対するチェック
			int cur_heap_idx = cur_CHUNK_idx << 7;
			int cur_len = ma_heap[cur_heap_idx];
			int rslt_len = cur_len + (int)pcs_add;

			// エラー顕在化
			if (rslt_len > 255) 
			{ throw new Exception("!!! DocHeap.Get_Heap_idx_for_LINE() : rslt_len > 255"); }

			// 以下の場合、CHUNK_idx の変更はない
			if (rslt_len <= 127 || cur_len >= 128)
			{
				ma_heap[cur_heap_idx] = (ushort)(rslt_len);
				return cur_heap_idx + 1;
			}

			// 1 CHUNK -> 2 CHUNK
			int new_heap_idx = Get_free_CHUNK_for_2CHUNK((ushort)rslt_len);
			if (new_heap_idx == cur_heap_idx)
			{
				ma_heap[cur_heap_idx] = (ushort)(rslt_len);
				return cur_heap_idx + 1;
			}

			// CHUNK の位置が変更されたため、CHUNK 内容のコピーが必要となる
			unsafe
			{
				fixed (ushort* pTop_heap = ma_heap)
				{
					ulong* pdst = (ulong*)(pTop_heap + new_heap_idx);
					ulong* psrc = (ulong*)(pTop_heap + cur_heap_idx);

					for (ushort* pEnd_dst = (ushort*)pdst + cur_len;; )
					{
						*pdst++ = *psrc++;
						if (pEnd_dst < pdst) { break; }
					}
				}
			}

			// new_heap_idx の使用文字数が、古い情報で上書きされたたため、もう一度新しい情報を書き込んでおく
			ma_heap[new_heap_idx] = (ushort)rslt_len;
			// 現在の CHUNK を未使用に戻す
			ma_heap[cur_heap_idx] = 0xffff;

			return new_heap_idx + 1;
		}
	}

	// ------------------------------------------------------------------------------------
	// 現時点では、未使用 CHUNK の探索は、先頭から順番に探すのみとしている。

	unsafe int Get_free_CHUNK_for_1CHUNK(in ushort rslt_len)
	{
		fixed (ushort* pTop_heap = ma_heap)
		{
			ushort* pheap = pTop_heap;
			for (ushort* pTmnt_heap = pTop_heap + EN_pcs_on_heap;; )
			{
				if (*pheap == 0xffff) { break; }
				pheap += EN_len_CHUNK;

				if (pheap == pTmnt_heap)
				{ throw new Exception("DocHeap.Get_free_Unit_idx_for_1unit() : バッファに空きがありませんでした。"); }
			}

			*pheap = rslt_len;
			return (int)(pheap - pTop_heap);  // Heap_idx を返す
		}
	}

	// ------------------------------------------------------------------------------------
	// 現時点では、未使用 CHUNK の探索は、先頭から順番に探すのみとしている。

	unsafe int Get_free_CHUNK_for_2CHUNK(in ushort rslt_len)
	{
		fixed (ushort* pTop_heap = ma_heap)
		{
			ushort* pheap = pTop_heap;
			for (ushort* pTmnt_heap = pTop_heap + EN_pcs_on_heap;; )
			{
				if (*pheap == 0xffff)
				{
					if (*(pheap + EN_len_CHUNK) == 0xffff) { break; }
					pheap += EN_len_CHUNK + EN_len_CHUNK;
				}
				else
				{ pheap += EN_len_CHUNK; }

				if (pheap == pTmnt_heap)
				{ throw new Exception("DocHeap.Get_free_Unit_idx_for_1unit() : バッファに空きがありませんでした。"); }
			}

			*pheap = rslt_len;
			*(pheap + EN_len_CHUNK) = 0;  // 使用中であることをマーク
			return (int)(pheap - pTop_heap);  // Heap_idx を返す
		}
	}
}

#endif

