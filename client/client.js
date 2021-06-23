'use strict';

const g_bデバッグ環境 = true;
const EN_str_ws_host_port = (g_bデバッグ環境 == true) ? "ws://127.0.0.1:80" : "ws://codeshare.pgw.jp:22225";
console.log('--- client.js スタート\r\nEN_str_ws_host_port -> ' + EN_str_ws_host_port);

let g_b_change_by_UpAllText = false;
let g_change_start_R_by_CMD = -1;

// 共通定数 ======================
const EN_bytes_WS_Buf = 16 * 1024;

// (byte) cmdID -> dc_idx -> (ushort) R -> C -> 行数
// 以降は、１行の文字数（0以上 255以下）の配列が続いて、その後、UTF16 での文字列が続く
// UTF16 の文字を代入する際は、偶数インデックスが要求されるため、文字列情報の前に padding が入ることがある
// なお、１行の文字数が 256 文字を超える場合は、不正な文字列として、そのユーザを切断する
// 一度に送出するのは「EN_bytes_WS_Buf」バイト以下にすること
const CMD_INSERT = 1;
const CMD_REMOVE = 2;

const CMD_Display_OK = 10;
const CMD_Chg_NumUsrs = 11;
const CMD_CLOSE_SERVER = 15;

// -------------------------------
// 128 ～ 255 は、暫定的なコマンド
const CMD_Req_Doc = 131;
// UP の場合 -> (byte) cmdID, 0 -> (ushort) cs_idx, 文字数, 文字列
// DOWN の場合 -> (byte) cmdID, 0 -> (ushort) 文字数, 文字列
const CMD_UpAllText = 135;
// ===============================

const g_ary_send_buf = new ArrayBuffer(EN_bytes_WS_Buf);
const g_u8_send_buf = new Uint8Array(g_ary_send_buf);
const g_u16_send_buf = new Uint16Array(g_ary_send_buf);

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

const g_e_div_editor = g_e_body.Add_DivTxt();
g_e_div_editor.id = 'editor';
g_e_div_editor.style.display = 'none';

const g_Ctrl_Pnl = new function() {
	const m_e_panel = g_e_body.Add_Div();
	m_e_panel.classList.add('Ctrl_pnl');
	
	const e_stg = m_e_panel.Add_FlexStg();
	
	const m_e_divTxt_NumUsrs = e_stg.Add_DivTxt('接続人数: ０人');
	m_e_divTxt_NumUsrs.classList.add('Info_divTxt');
	
	const m_e_btn_Test = e_stg.Add_Btn('テスト');
	m_e_btn_Test.onclick = () => {
		console.log(JSON.stringify(ace_document.getValue()));
	};

	const m_e_btn_ServerClose = e_stg.Add_Btn('サーバー終了');
	m_e_btn_ServerClose.onclick = () => {
		g_u8_send_buf[0] = CMD_CLOSE_SERVER;
		g_ws.send(new Uint8Array(g_ary_send_buf, 0, 1));
	};
	
	this.Chg_NumUsrs = (num) => {
		m_e_divTxt_NumUsrs.textContent = `接続人数: ${num}人`;
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
const g_ws = new WebSocket(EN_str_ws_host_port);
g_ws.binaryType = 'arraybuffer';

// --------------------
g_ws.onopen = () => {
	console.log('--- Websoket が open しました。');
};

g_ws.onclose = () => {
	console.log('--- WebSocket が close しました。');
};

g_ws.onmessage = (e) => g_WS_Reader.Interpret_ArrayBuffer(e.data);

g_ws.onerror = (e) => {
	console.log("!!! Websocket で onerror を補足しました。");
	console.log(e);
};

// ---------------------------------------------------------
// document 変更をサーバーに通知する設定
ace_session.on('change', (delta) => {
	if (g_b_change_by_UpAllText == true)
	{
		g_b_change_by_UpAllText = false;
		return;
	}
	
	// CMD による変更であるかどうかのチェック
	if (delta.start.row == g_change_start_R_by_CMD)
	{
		g_change_start_R_by_CMD = -1;
		return;
	}

	// 一度に送出するバイト数が EN_bytes_WS_Buf 以下であるか、また１行が 255文字以下であることをチェック
	// ***** 現時点では、分割送信をサポートしていない
	if (IsOK_bytes_lines(delta.lines) == false) { return; }

	// 変更をサーバーに通知する
	if (delta.action == 'insert')
	{
		Send_InsertDelta(delta);
	}
	else if (delta.action == 'remove')
	{
		Send_RemoveDelta(delta);
	}
	else {
		alert("不明な delta を検出しました。");
		console.log('!!! 不明な delta を検出しました。');
		console.log(delta);
	}
});

// ---------------------------------------------------------
function Send_InsertDelta(delta)
{
	g_u8_send_buf[0] = CMD_INSERT;
// g_u8_send_buf[1] = dc_idx;  // ドキュメントインデックス

	const bytes_to_send = WrtDelta_to_SendBuf(delta);
	console.log(`◀ CMD_INSERT を送信します。-> ${bytes_to_send} bytes`);
	
	// ++++++++++++++++++++++++++++++++
//	console.log(`--- 送信バイト数 -> ${bytes_to_send}`);
	// Firefox では、送信サイズが大きい場合、ここでエラーが発生する。理由は不明、、、
	g_ws.send(new Uint8Array(g_ary_send_buf, 0, bytes_to_send));
}

// ---------------------------------------------------------
function Send_RemoveDelta(delta)
{
	g_u8_send_buf[0] = CMD_REMOVE;
// g_u8_send_buf[1] = dc_idx;  // ドキュメントインデックス

	const bytes_to_send = WrtDelta_to_SendBuf(delta);
	console.log(`◀ CMD_REMOVE を送信します。-> ${bytes_to_send} bytes`);

	// ++++++++++++++++++++++++++++++++
//	console.log(`--- 送信バイト数 -> ${bytes_to_send}`);
	// Firefox では、送信サイズが大きい場合、ここでエラーが発生する。理由は不明、、、
	g_ws.send(new Uint8Array(g_ary_send_buf, 0, bytes_to_send));
}

// ---------------------------------------------------------
// delta の start、lines 情報を書き込む
// 戻り値： 送信すべきバイト数
function WrtDelta_to_SendBuf(delta)
{
	g_u16_send_buf[1] = delta.start.row;
	g_u16_send_buf[2] = delta.start.column;
	g_u16_send_buf[3] = delta.lines.length;

	// --------------------------------
	// 各行の文字数を書き込む
	let u8_pos_unused = 8;
	for (const line of delta.lines)
	{ g_u8_send_buf[u8_pos_unused++] = line.length; }
	
	// --------------------------------
	// 文字列を書き込む
	let u16_pos_unused = (u8_pos_unused + 1) >> 1;  // +1 は、配列境界の調整
	for (const line of delta.lines)
	{
		let idx_chr = 0;
		for (const len_line = line.length; idx_chr < len_line; )
		{
			g_u16_send_buf[u16_pos_unused++] = line.charCodeAt(idx_chr++);
		}
	}
	return u16_pos_unused << 1;
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
	
	// 8 : ヘッダの最長バイト数
	// * 2 : UTF-16 であるため、１文字につき２バイト
	// + 1 : 配列境界のための padding
	if (8 + lines.length + 1 + total_len_lines * 2 <= EN_bytes_WS_Buf)
	{ return true; }
	
	Violate_Rule("未対応：一度に送信する文字数が、バッファサイズを超えました、、、");
	return false;
}

