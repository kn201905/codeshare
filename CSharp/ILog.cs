using System;
using System.Text;
using System.Collections.Generic;

/////////////////////////////////////////////////////////////////////////////////////////

interface ILog
{
	void WrtLine(string str);
	void Wrt_Warning_Line(string str);
}

// ------------------------------------------------------------------------------------

delegate string CB_Proc_on_AppEnd();

interface IRslt_Log
{
	void Rgst_CB_Proc_on_AppEnd(CB_Proc_on_AppEnd cb_proc);
}

/////////////////////////////////////////////////////////////////////////////////////////

class SLog : ILog
{
	// --------------------------------------------------
	public void WrtLine(string str)
	{
		Console.WriteLine(str);
	}

	// --------------------------------------------------
	public void Wrt_Warning_Line(string str)
	{
		Console.WriteLine("\r\n### 警告 ###");
		Console.WriteLine(str);
	}
}

/////////////////////////////////////////////////////////////////////////////////////////

class Rslt_Log : IRslt_Log
{
	Stack<CB_Proc_on_AppEnd> m_stack_CB_Proc = new ();

	// --------------------------------------------------
	public void Rgst_CB_Proc_on_AppEnd(CB_Proc_on_AppEnd cb_proc)
	{
		m_stack_CB_Proc.Push(cb_proc);
	}

	// --------------------------------------------------
	public void Output_Rslt()
	{
		Console.WriteLine("\r\n=== Rslt_Log.Output_Rslt() の処理を開始します。===\r\n");

		foreach (CB_Proc_on_AppEnd cb_proc in m_stack_CB_Proc)
		{
			string str = cb_proc();
			Console.WriteLine(str);
			Console.WriteLine();  // 改行
		}
	}
}

