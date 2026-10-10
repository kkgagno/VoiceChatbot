using System;
using System.Runtime.InteropServices;

namespace VoiceChatbot;

/// <summary>
/// Checks once whether this PC has a Vulkan 1.2 GPU, before Whisper.net is allowed to load its Vulkan
/// runtime. Without one (no vulkan-1.dll, an old driver, only a software renderer) the Vulkan build
/// could fail inside native code, so the CPU runtime is used instead.
/// </summary>
internal static class VulkanProbe
{
    private const int StructureTypeInstanceCreateInfo = 1;
    private const int Success = 0;
    private const uint Vulkan12 = (1u << 22) | (2u << 12);
    private const int PropertiesSize = 1024; // VkPhysicalDeviceProperties is 824 bytes on x64
    private const int DeviceTypeOffset = 16;
    private const int DeviceNameOffset = 20;

    private const int MemoryHeapCountOffset = 260; // VkPhysicalDeviceMemoryProperties: after 32 memory types
    private const int MemoryHeapsOffset = 264;
    private const int MemoryHeapSize = 16;
    private const int MemoryPropertiesSize = 520;
    private const int HeapDeviceLocal = 1;

    private static readonly Lazy<(bool Found, string Name, GpuInfo? Gpu)> Result = new(Probe);

    /// <summary>True when a usable Vulkan GPU exists; <paramref name="name"/> is the one Whisper will use, or why there is none.</summary>
    public static bool HasGpu(out string name)
    {
        (var found, name, _) = Result.Value;
        return found;
    }

    /// <summary>
    /// The graphics card llama.cpp's Vulkan build uses (the first discrete one, else the first) with its
    /// video memory; null without Vulkan. Integrated graphics report shared system memory.
    /// </summary>
    public static GpuInfo? Gpu => Result.Value.Gpu;

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceCreateInfo
    {
        public int SType;
        public IntPtr Next;
        public uint Flags;
        public IntPtr ApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr EnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr EnabledExtensionNames;
    }

    [DllImport("vulkan-1.dll")]
    private static extern int vkEnumerateInstanceVersion(out uint apiVersion);

    [DllImport("vulkan-1.dll")]
    private static extern int vkCreateInstance(ref InstanceCreateInfo createInfo, IntPtr allocator, out IntPtr instance);

    [DllImport("vulkan-1.dll")]
    private static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);

    [DllImport("vulkan-1.dll")]
    private static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, [Out] IntPtr[]? devices);

    [DllImport("vulkan-1.dll")]
    private static extern void vkGetPhysicalDeviceProperties(IntPtr device, IntPtr properties);

    [DllImport("vulkan-1.dll")]
    private static extern void vkGetPhysicalDeviceMemoryProperties(IntPtr device, IntPtr memoryProperties);

    // The largest device-local memory heap: the card's own memory on a discrete GPU.
    private static long ReadVideoMemory(IntPtr device)
    {
        var memory = Marshal.AllocHGlobal(MemoryPropertiesSize);
        try
        {
            vkGetPhysicalDeviceMemoryProperties(device, memory);
            var heaps = Math.Min(16, Marshal.ReadInt32(memory, MemoryHeapCountOffset));
            long largest = 0;
            for (var h = 0; h < heaps; h++)
            {
                var offset = MemoryHeapsOffset + h * MemoryHeapSize;
                var size = Marshal.ReadInt64(memory, offset);
                var flags = Marshal.ReadInt32(memory, offset + 8);
                if ((flags & HeapDeviceLocal) != 0 && size > largest)
                    largest = size;
            }
            return largest;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static (bool, string, GpuInfo?) Probe()
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Vulkan probe is Windows-only", null);

        var instance = IntPtr.Zero;
        var properties = IntPtr.Zero;
        try
        {
            if (vkEnumerateInstanceVersion(out var version) != Success || version < Vulkan12)
                return (false, "Vulkan 1.2 is not available", null);

            var info = new InstanceCreateInfo { SType = StructureTypeInstanceCreateInfo };
            if (vkCreateInstance(ref info, IntPtr.Zero, out instance) != Success || instance == IntPtr.Zero)
                return (false, "no Vulkan driver", null);

            uint count = 0;
            if (vkEnumeratePhysicalDevices(instance, ref count, null) != Success || count == 0)
                return (false, "no Vulkan GPU found", null);
            var devices = new IntPtr[count];
            if (vkEnumeratePhysicalDevices(instance, ref count, devices) != Success)
                return (false, "no Vulkan GPU found", null);

            // Like ggml-vulkan: the first discrete GPU, else device 0.
            properties = Marshal.AllocHGlobal(PropertiesSize);
            GpuInfo? fallback = null;
            for (var i = 0; i < count; i++)
            {
                vkGetPhysicalDeviceProperties(devices[i], properties);
                var apiVersion = (uint)Marshal.ReadInt32(properties);
                var type = Marshal.ReadInt32(properties, DeviceTypeOffset); // 1 integrated, 2 discrete, 3 virtual, 4 CPU
                var name = Marshal.PtrToStringUTF8(properties + DeviceNameOffset) ?? "Vulkan GPU";
                var usable = apiVersion >= Vulkan12 && type is 1 or 2 or 3;
                if (usable && type == 2)
                    return (true, name, new GpuInfo(name, ReadVideoMemory(devices[i]), Discrete: true));
                if (i == 0 && usable)
                    fallback = new GpuInfo(name, ReadVideoMemory(devices[i]), Discrete: false);
            }

            return fallback != null ? (true, fallback.Name, fallback) : (false, "no Vulkan 1.2 GPU found", null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return (false, "Vulkan is not installed", null);
        }
        catch (Exception ex)
        {
            return (false, $"Vulkan check failed: {ex.Message}", null);
        }
        finally
        {
            if (properties != IntPtr.Zero)
                Marshal.FreeHGlobal(properties);
            if (instance != IntPtr.Zero)
            {
                try { vkDestroyInstance(instance, IntPtr.Zero); } catch { }
            }
        }
    }
}
