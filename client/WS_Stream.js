'use strict'

const g_WS_Reader = new function() {
	let m_ary_recv_buf = null;
	let m_u16_recv_buf = null;
	
	let m_len16_recv_buf = 0;
	let m_pos16_unread = 0;
	
	const m_delta_insert = {
		action: 'insert',
		start: { row: 0, column: 0 },
		end: { row: 0, column: 0 },
		lines: null
	};
	
	const m_delta_remove = {
		action: 'remove',
		start: { row: 0, column: 0 },
		end: { row: 0, column: 0 },
		lines: null
	};
	
	const m_empty_2lines = ['', ''];
	const m_empty_1line = '';

	// ---------------------------------------------------------
	this.Interpret_ArrayBuffer = (ary_recv_buf) => {
		m_ary_recv_buf = ary_recv_buf;
		m_u16_recv_buf = new Uint16Array(ary_recv_buf);
		
		m_len16_recv_buf = m_u16_recv_buf.length;
		m_pos16_unread = 0;
		
		for (;;)
		{
			Consume_Next();
			if (m_pos16_unread > m_len16_recv_buf)
			{
				console.log(`!!! m_pos16_unread -> ${m_pos16_unread} / m_len16_recv_buf -> ${m_len16_recv_buf}`);
				throw new Error('!!! g_WS_Reader.Interpret_ArrayBuffer() : m_pos16_unread > m_len16_recv_buf');
			}
			
			if (m_pos16_unread == m_len16_recv_buf) { break; }
		}

		// +++++++++++++++++++++++
	//	g_WS_Stream_toLog.Set_ArrayBuffer(ary_buf);
	//	g_WS_Stream_toLog.Show_atPos16(0);
	};
	
	// ---------------------------------------------------------
	const Consume_Next = () => {
		// cmdID と par_additional を consume
		const id16 = m_u16_recv_buf[m_pos16_unread++];
		const id = id16 & 0xff;
		const par = id16 >> 8;
		
		switch (id)
		{
		// --------------------------------------
		case CMD_Display_OK:
			g_Ctrl_Pnl.Chg_NumUsrs(par);
			g_e_div_editor.style.display = 'block';
			return;
			
		// --------------------------------------
		case CMD_Chg_NumUsrs:
			console.log("▶ CMD_Chg_NumUsrs を受信しました。");
			g_Ctrl_Pnl.Chg_NumUsrs(par);
			return;
			
		// --------------------------------------
		case CMD_Req_Doc: {
			console.log("▶ CMD_Req_Doc を受信しました。");
			const cs_idx = m_u16_recv_buf[m_pos16_unread++];
			
			g_u8_send_buf[0] = CMD_UpAllText;
			g_u8_send_buf[1] = 0;
						
			const u16str_document = ace_document.getValue();
			let len_str_document = u16str_document.length;

			let pos16_send_buf_unused = 1;
			g_u16_send_buf[pos16_send_buf_unused++] = cs_idx;
			g_u16_send_buf[pos16_send_buf_unused++] = len_str_document;
			
			let idx_chr = 0;
			while (len_str_document-- > 0)
			{
				g_u16_send_buf[pos16_send_buf_unused++] = u16str_document.charCodeAt(idx_chr++);
			}
			g_ws.send(new Uint8Array(g_ary_send_buf, 0, pos16_send_buf_unused * 2));
		} return;
		
		// --------------------------------------
		case CMD_UpAllText: {
			console.log('▶ CMD_UpAllText を受信しました。');
			let len_str_document = m_u16_recv_buf[m_pos16_unread++];
			
			if (len_str_document == 0) { return; }
			g_b_change_by_UpAllText = true;
			
			const u16_document = new Uint16Array(m_ary_recv_buf, m_pos16_unread * 2, len_str_document);
			const str_document = String.fromCharCode(...u16_document);
	
			ace_document.setValue(str_document);
			ace_editor.clearSelection();

			m_pos16_unread += len_str_document;  // バッファ consume
		} return;
		
		// --------------------------------------
		case CMD_INSERT:
			console.log('▶ CMD_INSERT を受信しました。');
			
			ReadDelta_frm_RecvBuf(m_delta_insert);
			ace_document.applyDelta(m_delta_insert);
			return;

		// --------------------------------------
		case CMD_REMOVE:
			console.log('▶ CMD_REMOVE を受信しました。');
			
			ReadDelta_frm_RecvBuf(m_delta_remove);
			ace_document.applyDelta(m_delta_remove);
			return;
		
		// --------------------------------------
		default:
			console.log("▶ 不明な id を受信しました。: id -> " + id);
			throw new Error('!!! 不明な id を検出しました。: id -> ' + id);
		}
	};

	// ---------------------------------------------------------
	const ReadDelta_frm_RecvBuf = (delta) => {
		delete delta.id;
		
		g_change_start_R_by_CMD = delta.start.row = m_u16_recv_buf[m_pos16_unread++];
		const start_column = delta.start.column = m_u16_recv_buf[m_pos16_unread++];
		const pcs_line = m_u16_recv_buf[m_pos16_unread++];

		const u8_pcs_ary = new Uint8Array(m_ary_recv_buf, m_pos16_unread * 2);
		
		if (pcs_line == 1)
		{
			m_pos16_unread++;  // 文字数情報を consume する
			
			const pcs_chrs = u8_pcs_ary[0];
			delta.end.row = g_change_start_R_by_CMD;
			delta.end.column = start_column + pcs_chrs;
			
			if (pcs_chrs == 1)
			{
				// １文字挿入の処理
				delta.lines = [String.fromCharCode(m_u16_recv_buf[m_pos16_unread++])];
				return;
			}
			
			delta.lines = [String.fromCharCode(...new Uint16Array(m_ary_recv_buf, m_pos16_unread * 2, pcs_chrs))];
			m_pos16_unread += pcs_chrs;  // 文字情報を consume する
			return;
		}
		
		if (pcs_line == 2)
		{
			if (u8_pcs_ary[0] == 0 && u8_pcs_ary[1] == 0)
			{
				// 改行だけの処理
				delta.end.row = g_change_start_R_by_CMD + 1;
				delta.end.column = 0;
				delta.lines = m_empty_2lines;
				
				m_pos16_unread++;  // 文字数情報を consume する（２bytes）
				return;
			}
		}

		// 複数行に渡る変更処理
		delta.end.row = g_change_start_R_by_CMD + pcs_line - 1;
		delta.end.column = u8_pcs_ary[pcs_line - 1];
		
		const lines = [];
		let pos_chrs_by_u8 = (m_pos16_unread + ((pcs_line + 1) >> 1)) << 1;
		for (let line = 0; line < pcs_line; ++line)
		{
			const pcs_chrs = u8_pcs_ary[line];
			if (pcs_chrs == 0)
			{
				lines.push(m_empty_1line);
			}
			else
			{
				lines.push(String.fromCharCode(...new Uint16Array(m_ary_recv_buf, pos_chrs_by_u8, pcs_chrs)));
				pos_chrs_by_u8 += (pcs_chrs << 1);
			}
		}
		delta.lines = lines;
		
		m_pos16_unread = pos_chrs_by_u8 >> 1;  // 文字情報を consume する
	};
};
