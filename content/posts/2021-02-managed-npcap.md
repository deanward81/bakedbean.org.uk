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

Under the hood OWL uses `libpcap` to create a virtual network adapter and uses raw frame manipulation from userspace code to talk to the underlying network stack. The project has a [great diagram](https://github.com/seemoo-lab/owl#architecture) detailing their interactions with the different parts of the system.

To get this whole shebang working under Windows we need a similar approach of capturing and manipulating raw network packets. I recalled that Wireshark used to install a packet capture driver that implemented the NDIS interfaces provided by Windows. Turns out the original way it did this was with [Winpcap](https://www.winpcap.org/) but there's a note saying that it has been deprecated in favour of [npcap](https://nmap.org/npcap/) which is part of the nmap project. Sadly the licensing for npcap is a little bit expensive if I felt like distributing this thing on the Windows Store - if that ever happens I'd probably consider writing a TAP driver much like what OpenVPN uses (see their [TAP driver repo](https://github.com/OpenVPN/tap-windows6)).

I should note that, prior to reading the OWL publications, I was under the impression that this might be implemented using something like [Wintun](https://www.wintun.net). This is similar in many ways to npcap but it works uses a TUN driver rather than a TAP one. TUN drivers operate at layer 3 of the OSI stack whereas TAP drivers operate at layer 2 - [wikipedia](https://en.wikipedia.org/wiki/TUN/TAP) has a nice diagram illustrating the differences. To implement AWDL we need to be able to manipulate radiotap and 802.11 on the WLAN interface that we are connected to - these are transport layer (i.e. L2) concerns so we need to use something that operates at that layer.

npcap is shipped as an installer that needs to be run in order to use it. The installer configures the underlying network driver. For our purposes we need to install it with the "Support raw 802.11 traffic (and monitor mode) for wireless adapters" option switched on:

<img src="/img/managed-npcap-1.png" width=640 alt="Installing npcap"><br/>
<sub style="color:lightgray">Installing npcap</sub>

I was about to sit down and write the [P/Invoke](https://docs.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke) code necessary to get .NET talking to this native library. That usually involves repetitive dissection of the header files provided with the library to construct the appropriate type signatures but, thankfully, the hard work has already been done by the maintainers of the [SharpPcap](https://github.com/chmorgan/sharppcap). This library provides us with a way of initiating a packet capture and manipulating packets as we see fit. Yay!

Additionally, there's [Packet.NET](https://github.com/chmorgan/packetnet) which provides a way to parse the raw packets that are being captured. This should be very helpful in allowing us to put together 

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








