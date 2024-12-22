﻿using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Referrer;

public class ReferrerCommand : Command
{
    public ReferrerCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("referrer", "Commands related to referrers")
    {
        AddCommand(new ListCommand(dockerRegistryClientFactory));
    }
}
