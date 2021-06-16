using System;

// 128文字／行 を基準として考える（１文字 = ushort）
// クライアント js の制限で、１行あたりの文字数の最大値は 255文字
// 1000行／Document を基準とする

// 以上の考え方で、1 Document ＝ 256 KBytes が基準

class Document
{
}

class DocHeap
{
	const int EN_len_CHUNK = 128;  // これは固定（7 bit シフトで idx を算出するようにしているため）
	// 最大でも 65535 以下とする。CHUNK_idx 0xffff は、未割り当てを示すため。
	const int EN_pcs_CHUNK = 1000;
	// 各 CHUNK の先頭は、利用している場合、文字数とする。未使用 CHUNK の先頭は 0xffff。
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


