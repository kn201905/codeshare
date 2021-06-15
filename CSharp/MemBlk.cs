#define USE_DBG_メンバ

using System;
using System.Collections.Generic;
using System.Diagnostics;  // Conditinal を利用するため

/////////////////////////////////////////////////////////////////////////////////////////

class MemBlk : IDisposable
{
	byte[] m_ary_buf;
	readonly MemBlk_Pool m_parent;

	// --------------------------------------------------
	public MemBlk(MemBlk_Pool parent, int bytes_ary_buf)
	{
		m_parent = parent;
		m_ary_buf = new byte[bytes_ary_buf];
	}

	// --------------------------------------------------
	public void Dispose()
	{
		m_parent.Return_MemBlk(this);
	}

	// --------------------------------------------------
	public byte[] Get_ary_buf() => m_ary_buf;
}

/////////////////////////////////////////////////////////////////////////////////////////

class MemBlk_Pool
{
	readonly int m_bytes_MemBlk;  // コンストラクタで設定
	Stack<MemBlk> m_stack_MemBlk = new Stack<MemBlk>();

	// --------------------------------------------------
	// デバッグ用
	int DBG_m_pcs_MemBlk_created = 0;
	[Conditional("USE_DBG_メンバ")]
	void DBG_Inc_pcs_MemBlk_created() => DBG_m_pcs_MemBlk_created++;

	int DBG_m_cnt_recycle = 0;
	[Conditional("USE_DBG_メンバ")]
	void DBG_Inc_cnt_recycle() => DBG_m_cnt_recycle++;

	string DBG_m_pool_name;

	// --------------------------------------------------
	// コンストラクタ

	public MemBlk_Pool(int bytes_MemBlk, string DBG_pool_name)
	{
		m_bytes_MemBlk = bytes_MemBlk;
		DBG_m_pool_name = DBG_pool_name;

		this.DBG_Rgst_CB_Proc_on_AppEnd();
	}

	// --------------------------------------------------
	public MemBlk Lease_MemBlk()
	{
		lock (m_stack_MemBlk)
		{
			if (m_stack_MemBlk.Count == 0)
			{
				this.DBG_Inc_pcs_MemBlk_created();
				return new MemBlk(this, m_bytes_MemBlk);
			}
			else
			{
				this.DBG_Inc_cnt_recycle();
				return m_stack_MemBlk.Pop();
			}
		}
	}

	// --------------------------------------------------
	public void Return_MemBlk(MemBlk mem_blk)
	{
		lock (m_stack_MemBlk)
		{
			m_stack_MemBlk.Push(mem_blk);
		}
	}

	// --------------------------------------------------
	public string DBG_Wrt_info_toILog()
	{
		return $"+++ MemBlk_Pool 情報 : {DBG_m_pool_name}\r\n"
			+ $"\tDBG_m_pcs_MemBlk_created -> {DBG_m_pcs_MemBlk_created}\r\n"
			+ $"\t現在スタックしている個数 -> {m_stack_MemBlk.Count}\r\n"
			+ $"\tDBG_m_cnt_recycle -> {DBG_m_cnt_recycle}\r\n";
	}

	// ==================================================
	// ログ用
	static ILog ms_iLog = null;
	IRslt_Log m_rslt_Log = null;

	[System.Runtime.CompilerServices.ModuleInitializer]
	public static void Module_Init()
	{
		ms_iLog = Program.Get_iLog();
	}

	// --------------------------------------------------
	[Conditional("USE_DBG_メンバ")]
	void DBG_Rgst_CB_Proc_on_AppEnd()
	{
		m_rslt_Log = Program.Get_rslt_Log();
		m_rslt_Log.Rgst_CB_Proc_on_AppEnd(DBG_Wrt_info_toILog);
	}
}
