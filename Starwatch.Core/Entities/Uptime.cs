﻿using Starwatch.Database;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Starwatch.Entities
{
    public class Uptime : IRecord
    {
        public string Table => "!uptime";

        public long Id { get; set; }
        public DateTime Started { get; set; }
        public DateTime? Ended { get; set; }
        public string LastLog { get; set; }
        public string Reason { get; set; }

        public Uptime()
        {
            Id = 0;
            Started = DateTime.UtcNow;
            Ended = null;
            LastLog = "";
            Reason = "";
        }

     

        public async Task<bool> LoadAsync(DbContext db)
        {
            Uptime uptime = await db.SelectOneAsync<Uptime>(Table, (reader) =>
            {
                return new Uptime()
                {
                    Id = reader.GetInt64("id"),
                    Started = reader.GetDateTime("date_started"),
                    Ended = reader.GetDateTimeNullable("date_ended"),
                    LastLog = reader.GetString("last_log"),
                    Reason = reader.GetString("reason")
                };
            }, new Dictionary<string, object>() { { "id", Id } });

            if (uptime == null)
                return false;

            Id = uptime.Id;
            Started = uptime.Started;
            Ended = uptime.Ended;
            LastLog = uptime.LastLog;
            Reason = uptime.Reason;
            return true;
        }

        public async Task<bool> SaveAsync(DbContext db)
        {
            Dictionary<string, object> args = new Dictionary<string, object>()
            {
                { "date_started", Started },
                { "date_ended", Ended },
                { "last_log", LastLog },
                { "reason", Reason },
            };

            if (Id > 0) args.Add("id", Id);

            //Insert the database
            var insertedId = await db.InsertUpdateAsync(Table, args);
            if (insertedId > 0)
            {
                Id = insertedId;
                return true;
            }

            return false;

        }

        /// <summary>
        /// Ends any uptime sessions that still exist.
        /// </summary>
        /// <param name="db"></param>
        /// <returns></returns>
        public static async Task<int> EndAllAsync(DbContext db)
        {
            var cmd = await db.CreateCommandAsync("UPDATE !uptime SET date_ended = CURRENT_TIMESTAMP WHERE date_ended IS NULL");
            try
            {
                if (cmd == null) return 0;
                return await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception)
            {
                return 0;
            }
            finally
            {
                db.ReleaseCommand();
            }
        }
    }
}