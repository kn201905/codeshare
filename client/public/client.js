'use strict';

// 共通定数 ======================
const EN_bytes_WS_Buf = 16 * 1024;

// コマンドの上位４bit は、パラメータ長のフラグとする
// パラメータ長が 16bit となるとき、「１」となる。16 bit値は、LE で格納される。
const CMD_CONNECTCMD_LOAD = 0;
// 挿入位置 (R, C)、行数
// 以降は、１行の文字数（0 以上）の配列が続いて、その後、UTF16 での文字列が続く
// UTF16 の文字を代入するときは、偶数インデックスが要求されるため、文字列情報の前に padding が入ることがある
// なお、１行の文字数が 256 文字を超える場合は、不正な文字列として、そのユーザを切断する
// 一度に送出するのは「EN_bytes_WS_Buf」バイト以下にすること
const CMD_INSERT = 1;
const CMD_REMOVE = 2;  // 削除開始位置 (R, C)、終了位置 (R, C)

const CMD_CLOSE_SERVER = 15;

// ===============================

const g_ary_send_buf = new ArrayBuffer(EN_bytes_WS_Buf);
const g_byte_view_send_buf = new Uint8Array(g_ary_send_buf);

// ---------------------------------------------------------
const g_e_body = document.body;

Element.prototype.Add_Div = function() {
	const e_div = document.createElement('div');
	this.appendChild(e_div);
	return e_div;
};

Element.prototype.Add_DivTxt = function(txt) {
	const e_div = document.createElement('div');
	e_div.textContent = txt;
	this.appendChild(e_div);
	return e_div;
};

Element.prototype.Add_Btn = function(label) {
	const e_btn = document.createElement('button');
	e_btn.textContent = label;
	this.appendChild(e_btn);
	return e_btn;
};

Element.prototype.Add_FlexStg = function() {
	const e_div = document.createElement('div');
	e_div.style.display = 'flex';
	// 'wrap'：flexbox が複数行に折返しとなる（単一行にしたい場合はこの指定を外すこと）
	e_div.style.flexWrap = 'wrap';
	this.appendChild(e_div);
	return e_div;
};

const g_Ctrl_Pnl = new function() {
	const m_e_panel = g_e_body.Add_Div();
	m_e_panel.classList.add('Ctrl_pnl');
	
	const e_stg = m_e_panel.Add_FlexStg();
	
	const m_e_divTxt_Info = e_stg.Add_DivTxt('接続人数: １人');
	m_e_divTxt_Info.classList.add('Info_divTxt');
	
	const m_e_btn_ServerClose = e_stg.Add_Btn('サーバー終了');
	m_e_btn_ServerClose.onclick = () => {
		g_byte_view_send_buf[0] = CMD_CLOSE_SERVER;
		g_ws.send(new Uint8Array(g_ary_send_buf, 0, 1));
	};
};

// ---------------------------------------------------------
const ace_editor = ace.edit("editor");
ace_editor.setTheme("ace/theme/dracula");
ace_editor.setShowPrintMargin(false);
const ace_session = ace_editor.getSession();
ace_session.setNewLineMode("windows");
const ace_document = ace_session.getDocument();

// --------------------
const EN_str_ws_host_port = "ws://127.0.0.1:80";
const g_ws = new WebSocket(EN_str_ws_host_port);

// --------------------
g_ws.onopen = () => {
	console.log('--- Websoket が open しました。');
};

g_ws.onclose = () => {
	console.log('--- WebSocket が close しました。');
};

g_ws.onmessage = (e) => {
//	console.log( Object.prototype.toString.apply(e.data) );
};

ace_session.on('change', (delta) => {
	if (delta.action == 'insert') { Ace_insert(delta); }
	else if (delta.action == 'remove') { Ace_remove(delta); }
	else {
		alert("不明な delta を検出しました。");
		console.log('!!! 不明な delta を検出しました。');
		console.log(delta);
	}
});

// delta の row, column は 0 スタート
function Ace_insert(delta)
{
	console.log(delta);
	
	// まず送出バイト数をチェックする
	let cmd = CMD_INSERT;
	let idx_buf = 1;
	
	let bytes_wrtn = Set_header_val(idx_buf, delta.start.row, "delta.start.row")
	if (bytes_wrtn < 0) { return; }
	if (bytes_wrtn == 2) { cmd |= 0x80; }
	idx_buf += bytes_wrtn;
	
	bytes_wrtn = Set_header_val(idx_buf, delta.start.column, "delta.start.column")
	if (bytes_wrtn < 0) { return; }
	if (bytes_wrtn == 2) { cmd |= 0x40; }
	idx_buf += bytes_wrtn;

	// 一度に送出するバイト数が EN_bytes_WS_Buf 以下であるかをチェック
	// ***** 現時点では、分割送信をサポートしていない
	if (IsOK_bytes_lines(delta.lines) == false) { return; }

	bytes_wrtn = Set_header_val(idx_buf, delta.lines.length, "delta.lines.length")
	if (bytes_wrtn < 0) { return; }
	if (bytes_wrtn == 2) { cmd |= 0x20; }
	idx_buf += bytes_wrtn;
	
	// コマンドの記録
	g_byte_view_send_buf[0] = cmd;
	
	// --------------------------------
	for (const line of delta.lines)
	{ g_byte_view_send_buf[idx_buf++] = line.length; }
	
	if (idx_buf & 1) { idx_buf++; }  // 配列境界の調整（padding）
	const ary_u16 = new Uint16Array(g_ary_send_buf, idx_buf);
		
	let idx_for_u16 = 0;
	for (const line of delta.lines)
	{
		let idx_chr = 0;
		for (const len_line = line.length; idx_chr < len_line; )
		{
			ary_u16[idx_for_u16++] = line.charCodeAt(idx_chr++);
		}
	}
	
	const bytes_send = idx_buf + idx_for_u16 * 2;
	g_ws.send(new Uint8Array(g_ary_send_buf, 0, bytes_send));
}

// --------------------------------
function Ace_remove(delta)
{
	console.log(delta);
}

// --------------------------------
// Violation があった場合、-1 が返される。
function Set_header_val(idx_buf, val_header, val_name)
{
	if (val_header < 256)
	{
		g_byte_view_send_buf[idx_buf] = val_header;
		return 1;
	}
	else
	{
		if (val_header >= 65535)
		{
			Violate_Rule("次の操作はできません： " + val_name + " >= 65535");
			return -1;
		}
		
		g_byte_view_send_buf[idx_buf] = val_header & 0xff;
		g_byte_view_send_buf[idx_buf + 1] = val_header >>> 8;
		return 2;
	}
}

// --------------------------------
function Violate_Rule(msg)
{
	alert(msg + "\r\nサーバーとの接続を切断します。");
	g_ws.close();
}

// --------------------------------
// bytes がバッファを超える場合、false が返される
function IsOK_bytes_lines(lines)
{
	let total_len_lines = 0;
	for (const line of lines)
	{
		if (line.length > 255)
		{
			Violate_Rule("１行の文字数を 256文字以上にすることは、禁止されています。");
			return false;
		}
		total_len_lines += line.length;
	}
	
	// 7 : ヘッダの最長バイト数
	// * 2 : UTF-16 であるため、１文字につき２バイト
	// + 1 : 配列境界のための padding
	if (7 + lines.length + 1 + total_len_lines * 2 <= EN_bytes_WS_Buf)
	{ return true; }
	
	Violate_Rule("未対応：一度に送信する文字数が、バッファサイズを超えました、、、");
	return false;
}
