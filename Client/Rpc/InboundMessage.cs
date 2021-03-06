﻿using System;
using System.Collections.Generic;

namespace NFive.Client.Rpc
{
	public class InboundMessage
	{
		public string Event { get; set; }

		public List<string> Payloads { get; set; }

		public DateTime Created { get; set; }

		public DateTime Sent { get; set; }

		public DateTime Received { get; set; } = DateTime.UtcNow;
	}
}
