﻿using System;
using static Starwatch.Starbound.Server;

namespace Starwatch.Starbound
{
    public struct Statistics
    {
        public int Connections { get; set; }
        public int? LastConnectionID { get; set; }
        public MemoryUsage MemoryUsage { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double Uptime { get; set; }

        public long CurrentBanTicket { get; set; }
        public int TotalAccounts { get; set; }

        public string LastShutdownReason { get; set; }

        internal Statistics(Server server, MemoryUsage usage)
        {
            LastShutdownReason = server.LastShutdownReason;

            Connections = server.Connections.Count;
            LastConnectionID = server.Connections.LastestPlayer?.Connection;
            StartTime = server.StartTime;
            EndTime = server.EndTime;

            TotalAccounts = server.Settings.Accounts.Accounts.Count;
            CurrentBanTicket = server.Settings.CurrentBanTicket;

            Uptime = 0;
            if (EndTime > StartTime) { Uptime = -(DateTime.UtcNow - EndTime).TotalSeconds; }
            if (StartTime > EndTime) { Uptime = (DateTime.UtcNow - StartTime).TotalSeconds; }
            MemoryUsage = usage;
        }
    }
}
