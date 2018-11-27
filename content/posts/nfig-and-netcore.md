---
title: "Integrating NFig with .NET Core"
date: 2018-11-29T00:00:00Z
draft: true
---

Over the past few months we've been busy laying the groundwork needed to support migrating Stack Overflow to [ASP.NET Core](https://docs.microsoft.com/en-us/aspnet/core/?view=aspnetcore-2.1). Along the way that's included:

 - Porting most of our open source libraries to netstandard20 so that they can be used readily across net462 and netcoreapp21 projects
 - Migrating from Linq2Sql to EF Core (and [bringing down most of the Stack Exchange Network in the process](https://twitter.com/Nick_Craver/status/1047925037007884288))
 - Using the built-in `ManagedWebSocket` and Kestrel instead of [NetGain](https://github.com/StackExchange/NetGain) for our web sockets (still a work in progress - we have some issues with GC).
 - Tweaking how we do localization to move from a precompilation model to a runtime-based model.

 We've also started some greenfield projects that aren't public-facing but that support critical parts of our business. That's helped us put together a list of 'standard' libraries that we'll likely use across some of our other applications when we migrate them to .NET Core. One of those has been [NFig](https://github.com/NFig/NFig) - a way to manage configuration settings for our applications.

## NFig

 NFig was originally written by [Bret Copeland](https://bret.codes/) and [Bryan Ross](https://rossipedia.com/) for the ad server that powers our job ads and lately we've been using it in other applications. It works by allowing us to define our configuration settings in code and then allowing us to change values at runtime by storing overrides in a backing store. Here's an example configuration class:

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
 if (Current.Settings.FeatureFlags.EnableWhizzband)
 {
     // do whizzbang stuff!
 }
 ```

 NFig surfaces this is in configuration views provided by the NFig.UI package:

![NFig List]

We can override this at runtime for a specific data centre or everywhere and that value will be immediately reflected the next time it's accessed by the code:

![NFig Setting]

This is great and works extremely well - it allows us to do things like deploy features behind a flag and then switch them on at runtime without having to re-deploy the application.

However, .NET Core comes with its own set of APIs (i.e. the [`Options` pattern](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options?view=aspnetcore-2.1)) for providing upto date configuration settings and ideally we'd like to be as "native" as possible.

## .NET Core & NFig



[NFig List]: /img/nfig-and-netcore-1.png "NFig List View"
[NFig Setting]: /img/nfig-and-netcore-2.png "NFig Setting View"