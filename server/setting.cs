#define テスト環境

/////////////////////////////////////////////////////////////////////////////////////////
static class Setting
{
#if テスト環境
	public const string EN_public_folder = @"E:/__dev__SRC__/codeshare/public";

	public static System.Net.IPAddress ms_Address_for_TcpListener = System.Net.IPAddress.Parse("127.0.0.1");
	public const int NUM_html_port = 80;
#else
	public const string EN_public_folder = @"/home/codeshare/public";

	public static System.Net.IPAddress ms_Address_for_TcpListener = System.Net.IPAddress.Any;
	public const int NUM_html_port = 22225;
#endif

	public const int NUM_seconds_html_keep_alive = 5;
	public const int NUM_msec_html_keep_alive = 5 * 1000;
}


/////////////////////////////////////////////////////////////////////////////////////////

static class Common
{
	public const int EN_bytes_WS_Buf = 16 * 1024;  // 16 KBytes

	public const byte CMD_INSERT = 1;
	public const byte CMD_REMOVE = 2;

	public const byte CMD_Display_OK = 10;
	public const byte CMD_Chg_NumUsrs = 11;
	public const byte CMD_CLOSE_SERVER = 15;

	// -------------------------------------------
	// 128 ～ 255 は、暫定的なコマンド
	public const byte CMD_Req_Doc = 131;
	// cmdID, 0 の以降は ushort で cs_idx, 文字数, 文字列、、、 と続く
	public const byte CMD_UpAllText = 135;

	// ==================================================
	[System.Runtime.CompilerServices.ModuleInitializer]
	public static void Module_Init()
	{
		if (
			CMD_INSERT != (byte)CS_ID.CMD_INSERT
			|| CMD_REMOVE != (byte)CS_ID.CMD_REMOVE
			|| CMD_Display_OK != (byte)CS_ID.CMD_Display_OK
			|| CMD_Chg_NumUsrs != (byte)CS_ID.CMD_Chg_NumUsrs
			|| CMD_Req_Doc != (byte)CS_ID.CMD_Req_Doc
			|| CMD_UpAllText != (byte)CS_ID.CMD_UpAllText
		)
		{ throw new System.Exception("!!! Common.Module_Init() : Common と CS_ID の定数に不一致を検出しました。"); }
	}
}
