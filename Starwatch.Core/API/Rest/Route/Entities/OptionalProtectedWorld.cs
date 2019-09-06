﻿using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Starwatch.Modules.Whitelist;
using System.Collections.Generic;

namespace Starwatch.API.Rest.Route.Entities
{
    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    public struct OptionalProtectedWorld
    {
        public string Name { get; set; }
        public string Whereami { get; set; }
        public HashSet<string> AccountList { get; set; }
        public bool? AllowAnonymous { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public WhitelistMode? Mode { get; set; }
        
        public OptionalProtectedWorld(ProtectedWorld pw)
        {
            Name = pw.Name;
            Whereami = pw.World.Whereami;

            AllowAnonymous = pw.AllowAnonymous;
            Mode = pw.Mode;

            //TODO: Fix the account listing
            AccountList = null;
        }
    }
}
