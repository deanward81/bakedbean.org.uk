---
title: "Integrating NFig with .NET Core"
date: 2018-11-29T00:00:00Z
draft: true
---
Over the past few weeks we've been busy laying the groundwork needed to support migrating Stack Overflow to [ASP.NET Core](https://docs.microsoft.com/en-us/aspnet/core/?view=aspnetcore-2.1).

Our roadmap for the migration extends out to next year, but we've been tackling a lot of pre-requisites:

<!--more-->

- Porting most of our open source and internal libraries to netstandard20 so that they can be used readily across net462 and netcoreapp2x projects
- Migrating from Linq2Sql to EF Core (and [bringing down most of the Stack Exchange Network in the process](https://twitter.com/Nick_Craver/status/1047925037007884288))
- Using the built-in `ManagedWebSocket` and Kestrel instead of [NetGain](https://github.com/StackExchange/NetGain) for our web socket server.
- [Tweaking how we do localization](https://m0sa.net/posts/2018-11-runtime-moonspeak/) to move from a precompilation model to a runtime-based model.

We've also started some greenfield projects that aren't public-facing but that support critical parts of our business. That's helped us put together a list of 'standard' libraries that we'll likely use across some of our other applications when we migrate them to .NET Core. One of those has been [NFig](https://github.com/NFig/NFig) - a way to manage configuration settings for our applications.

## NFig - Some Background

NFig was originally written by [Bret Copeland](https://bret.codes/) and [Bryan Ross](https://rossipedia.com/) for the ad server that powers our job ads. After a while we started using it in other applications such as [Stack Overflow Talent](https://talent.stackoverflow.com/) and [Stack Overflow Jobs](https://stackoverflow.com/jobs/). It works by allowing us to define our configuration settings in code and then allowing us to change those settings at runtime by storing overrides in a backing store. Here's an example configuration class:

```c#
public class Settings
{
    [SettingsGroup]
    public FeatureFlagSettings FeatureFlags { get; private set; }

    public class FeatureFlagSettings
    {
        [Description("Enables the [Whizzbang](https://trello.com/.../) feature.")]
        [Setting(false)]
        [TieredDefaultValue(Tier.Local, true)]
        [TieredDefaultValue(Tier.Dev, true)]
        public bool EnableWhizzbang { get; private set; }
    }
}
```

Everywhere that we want to use the Whizzbang feature we can check this flag:

```c#
if (Current.Settings.FeatureFlags.EnableWhizzbang)
{
     // do whizzbang stuff!
}
```

NFig surfaces this is in configuration views provided by the NFig.UI package:

![NFig List]

We can override this at runtime for a specific data centre or everywhere and that value will be immediately reflected the next time it's accessed by the code:

![NFig Setting]

This is great and works extremely well - it allows us to do things like deploy features behind a flag and then switch them on at runtime without having to re-deploy the application.

## .NET Core & NFig

.NET Core comes with its own set of APIs (known as the [`Options` pattern](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options?view=aspnetcore-2.1)) for providing upto date configuration settings. An `IOptions` implementation is configured based upon code such as this in an application's startup:

```c#
public class Startup
{
    private readonly IConfiguration _configuration;

    public Startup(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.Configure<MyOptions>(_configuration.GetSection("MyOptions"));
    }
}
```

This uses the configuration subsystem to compose the options using anything from a JSON file to a SQL Server or a secure storage mechanism for things like secrets.

Ideally we'd like using NFig to be as "native" as possible, so, our first task is to make sure settings provided by NFig can be resolved as `IOptions` implementations. We can do that in our application startup:

```c#
public enum Tier
{
    Local,
    Dev,
    Test,
    Prod,
}

public enum DataCentre
{
    Local,
    Timbuktu,    
}

public class Startup
{
    private readonly IConfiguration _configuration;

    public Startup(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddNFig<Settings, Tier, DataCenter>();
    }
}
```

You'll notice that we've specified a concrete type for our settings (`Settings`) and also enum types for the `Tier` and `DataCentre`. The latter two allow our application to specify different settings per data centre and tier.

Now, instead of using a static reference to `Settings.FeatureFlags.EnableWhizzbang`, we can inject an `IOptions<FeatureFlagSettings>` into our consuming class:

```c#
public class HomeController : Controller
{
    private readonly FeatureFlagSettings _featureFlags;

    public HomeController(IOptions<FeatureFlagSettings> featureFlags)
    {
        _featureFlags = featureFlags.Value;
    }

    public IActionResult Home()
    {
        if (_featureFlags.EnableWhizzbang)
        {
            return Content("Whizzbang enabled!");
        }

        return Content("Whizzbang disabled");
    }
}
```

## Exposing NFig UI

We've also added middleware that handles the rendering of the NFig UI views used for managing overrides:

```c#
public class Startup
{
    // ... other startup bits here

    public void Configure(IAppBuilder appBuilder, IHostingEnvironment hostingEnvironment)
    {
        services.AddNFig<Settings, Tier, DataCenter>();
    }
}
```

## Advanced Functionality

We can even go so far as configuring

[NFig List]: /img/nfig-and-netcore-1.png "NFig List View"
[NFig Setting]: /img/nfig-and-netcore-2.png "NFig Setting View"