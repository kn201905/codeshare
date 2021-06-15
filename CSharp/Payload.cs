using System;
using System.Threading;

static class Payload
{
	static Tools.Boxed<bool> msb_IsCalled_ServerClose = new (false);

	// ------------------------------------------------------------------------------------
	public static unsafe void Read(byte* pbuf, int len)
	{
		switch (*pbuf)
		{
		case Common.CMD_CLOSE_SERVER:
			// ThreadProc_ServerClose_by_WS_Context() をコールするのを１回だけにするための措置
			lock (msb_IsCalled_ServerClose)
			{
				if (msb_IsCalled_ServerClose.m_val == true) { return; }

				msb_IsCalled_ServerClose.m_val = true;
				(new Thread(Server.ThreadProc_ServerClose_by_WS_Context)).Start();
			}
			break;
		}
	}
}
