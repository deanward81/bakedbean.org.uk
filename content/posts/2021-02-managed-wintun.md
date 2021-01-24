---
title: "Using Wintun from C#"
date: 2021-02-10T15:00:00Z
tags: [.net, wintun, networking]
images: [img/iconfiguration-cover.png]
---

I've been toying with the idea of implementing a network protocol using C# and .NET for a while now. I've been frustrated at the file transfer dance from an Apple device to Windows - when transferring between Apple devices it's dead simple by using AirDrop. Ideally AirDrop would work natively with Windows but I can't ever see Apple enabling that scenario.

So I've decided to embark upon a side-project, that might amount to nothing, but, hey, it could be an interesting learning experience, so let's give it a shot!

This is part one of an, as yet, undetermined number of posts documenting that journey!

## Breaking down the problem

Turns out AirDrop uses a protocol called Apple Wireless Direct Link (AWDL) and a whole lot of people have been working to reverse engineer it. That work has resulted in the [OWL project](https://owlink.org) which has a bunch of publications breaking down the protocol as well as open source code in C and Python that provide a reference implementation of that protocol (called OWL - Open Wireless Link).

However OWL is written to work on MacOS and Linux - it makes use of `libpcap` and various libraries that only work on those platforms.

Under the hood OWL creates a virtual network adapter and uses raw frame manipulation from userspace code to talk to the underlying network stack. The project has a [great diagram](https://github.com/seemoo-lab/owl#architecture) detailing their interactions with the different parts of tbe system.

To get this whole shebang working under Windows we need a similar virtual device that we can use to mess with raw frames. Windows has APIs to do this but, frankly, they're hard to work with because of driver signing requirements. [WireGuard](https://www.wireguard.com) found these requirements to be a bit much so they released an open source library called [Wintun](https://www.wintun.net) that allows userspace virtual network devices to be created. Yay!

Wintun is shipped as a DLL for various CPU architectures and they provide their API as a [C header file](https://git.zx2c4.com/wintun/tree/api/wintun.h). Typical usage involves dynamically loading the DLL and then using `GetProcAddress` to get a pointer to each of the functions in the API. In .NET land that means using [P/Invoke](https://docs.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke) to perform the interop and marshalling between native and .NET types for us. To do so we need to break down the API into a set of .NET P/Invoke signatures that we can use to interact with Wintun.

First task then: turn the Wintun header file into P/Invoke method and type definitions in C#. Let's get to it!

## Diving into code

First I'm going to download the binaries of Wintun.  I'll need the x64 binaries from that ZIP file. Longer term, assuming this works, I'll create a NuGet package that can target different architectures, but let's keep it simple for now.

Next, let's start to break down the header file describing the API. We'll work through each function and its types and use that to construct the equivalent C#. Full disclosure: first time I've needed to do this in anger so let's see how wrong I get it!

Most functions in the API deal with a type called `WINTUN_ADAPTER_HANDLE` which is defined as `typedef void* WINTUN_ADAPTER_HANDLE`. P/Invoke  treats `void*` as an `IntPtr` - nice and simple!

Next, our first function is `WINTUN_CREATE_ADAPTER_FUNC` which is defined as:

```c
typedef _Return_type_success_(return != NULL) WINTUN_ADAPTER_HANDLE(WINAPI *WINTUN_CREATE_ADAPTER_FUNC)(
    _In_z_ const WCHAR *Pool,
    _In_z_ const WCHAR *Name,
    _In_opt_ const GUID *RequestedGUID,
    _Out_opt_ BOOL *RebootRequired);
```

There's some superfluous bits in here - notably the return type annotation (`_Return_type_success_(return != NULL)`) and the parameter   annotations (`_In_z_`,  `_In_opt_` and `_Out_opt_`). These are part of Microsoft's SAL or [source-code annotation language](https://docs.microsoft.com/en-us/cpp/c-runtime-library/sal-annotations?view=msvc-160). This is used by the VC compiler to help indicate how a function uses its parameters and return types. .NET could care less, although they _are_ helpful in determining some aspects of our method signatures.








