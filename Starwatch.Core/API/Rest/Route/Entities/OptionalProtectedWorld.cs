﻿using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Starwatch.Extensions.Backup;
using Starwatch.Extensions.Whitelist;
using System;
using System.Collections.Generic;
using System.Text;

namespace Starwatch.API.Rest.Route.Entities
{
    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    public struct OptionalProtectedWorld
    {
        public string Whereami { get; set; }
        public HashSet<string> AccountList { get; set; }
        public bool? AllowAnonymous { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public WhitelistMode? Mode { get; set; }
        
        public OptionalProtectedWorld(ProtectedWorld pw)
        {
            Whereami = pw.World.Whereami;
            AccountList = pw.AccountList;
            AllowAnonymous = pw.AllowAnonymous;
            Mode = pw.Mode;
        }
    }
}
