﻿using Newtonsoft.Json;
using Starwatch.Database;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;

namespace Starwatch.Entities
{
    [JsonObject(MemberSerialization = MemberSerialization.OptIn)]
    public class Account : IRecord
    {
        public const string Annonymous = "<annonymous>";
        public string Table => "!accounts";

        [JsonProperty]
        public string Name { get; private set; }
        
        [JsonProperty]
        public bool IsAdmin { get; set; }

        [JsonProperty]
        public string Password { get; set; }

        [JsonProperty]
        public DateTime LastSeen { get; private set; }

        [JsonProperty]
        public bool IsActive { get; set; }

        private Account() { this.Name = "unnamed"; }
        public Account(string name)
        {
            this.Name = name;
        }

        public Account(Account acc)
        {
            Name = acc.Name;
            IsAdmin = acc.IsAdmin;
            Password = acc.Password;
            LastSeen = acc.LastSeen;
            IsActive = true;
        }

        /// <summary>
        /// Updates the last seen value to now
        /// </summary>
        public void UpdateLastSeen() { LastSeen = DateTime.UtcNow; }

        public async Task<bool> LoadAsync(DbContext db)
        {
            var acc = await db.SelectOneAsync(Table, (reader) => FromDbDataReader(db, reader), new Dictionary<string, object>() { { "name", Name } });
            if (acc == null) return false;

            this.Name = acc.Name;
            this.Password = acc.Password;
            this.LastSeen = acc.LastSeen;
            this.IsActive = acc.IsActive;
            this.IsAdmin = acc.IsAdmin;
            return true;
        }

        public static async Task<List<Account>> LoadAllActiveAsync(DbContext db)
        {
            return await db.SelectAsync<Account>("!accounts", (reader) => FromDbDataReader(db, reader), new Dictionary<string, object>() { { "server", 1 } });
        }

        private static Account FromDbDataReader(DbContext db, DbDataReader reader)
        {
            return new Account()
            {
                Name = reader.GetString("name"),
                Password = db.Decrypt(reader.GetString("password")),
                IsAdmin = reader.GetBoolean("is_admin"),
                LastSeen = reader.GetDateTime("last_seen"),
                IsActive = reader.GetBoolean("is_active")
            };
        }

        public async Task<bool> SaveAsync(DbContext db)
        {
            if (Name.Length >= 64)
                throw new Exception("Account name cannot exceed 64 characters.");

            return 0 != await db.InsertUpdateAsync(Table, new Dictionary<string, object>()
            {
                { "name", Name },
                { "password", db.Encrypt(Password) },
                { "is_admin", IsAdmin },
                { "last_seen", LastSeen },
                { "is_active", IsActive }
            });
        }
    }
    
    /// <summary>
    /// Account Storage Structure
    /// </summary>
    public class AccountList 
    {
        private Dictionary<string, Account> _accounts = new Dictionary<string, Account>();
        public IReadOnlyDictionary<string, Account> Accounts => _accounts;

        /// <summary>
        /// Sets all the accounts to the enumerable
        /// </summary>
        /// <param name="accounts"></param>
        public void SetAccounts(IEnumerable<Account> accounts)
        {
            _accounts = accounts.ToDictionary(a => a.Name);
        }
    }
}
