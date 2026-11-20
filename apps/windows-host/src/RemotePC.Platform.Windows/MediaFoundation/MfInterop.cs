// Minimal Media Foundation COM interop. Vtable order follows mfobjects.h / mftransform.h / strmif.h exactly.
// Unused slots are declared as placeholders (never called) so that later slots line up.
#pragma warning disable CA1707, CA1712, CA1028, CA1008, CA1069, CA1711, CA1720, CA1401, SYSLIB1054, SYSLIB1096
using System.Runtime.InteropServices;

namespace RemotePC.Platform.Windows.MediaFoundation;

public sealed class MediaFoundationException : Exception
{
    public MediaFoundationException() { }
    public MediaFoundationException(string message) : base(message) { }
    public MediaFoundationException(string message, Exception inner) : base(message, inner) { }
    public MediaFoundationException(string message, int hr) : base(message) => HResult = hr;
}

internal static class MfGuids
{
    public static readonly Guid CategoryVideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid FormatNV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid FormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid FormatHEVC = new("43564548-0000-0010-8000-00AA00389B71");
    public static readonly Guid FormatAV1 = new("31305641-0000-0010-8000-00AA00389B71");

    public static readonly Guid MT_MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MT_Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MT_FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MT_FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MT_PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MT_InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MT_AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MT_Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MT_MpegSequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");

    public static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    public static readonly Guid TransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static readonly Guid EnumHardwareUrl = new("2fb866ac-b078-4942-ab6c-003d05cda674");
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    public static readonly Guid CodecApi_RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid CodecApi_MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid CodecApi_MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid CodecApi_GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid CodecApi_BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid CodecApi_LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid CodecApi_ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static readonly Guid CodecApi_QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");

    public static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
}

internal static class MfConst
{
    public const int MF_VERSION = 0x00020070;
    public const uint MFT_ENUM_FLAG_SYNCMFT = 0x1, MFT_ENUM_FLAG_ASYNCMFT = 0x2, MFT_ENUM_FLAG_HARDWARE = 0x4, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
    public const int MFT_MESSAGE_COMMAND_FLUSH = 0, MFT_MESSAGE_COMMAND_DRAIN = 1;
    public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000, MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    public const int MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002, MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
    public const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100, MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    public const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);
    public const int MF_E_ATTRIBUTENOTFOUND = unchecked((int)0xC00D36E6);
    public const uint MF_EVENT_FLAG_NO_WAIT = 1;
    public const int METransformNeedInput = 601, METransformHaveOutput = 602, METransformDrainComplete = 603;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftRegisterTypeInfo
{
    public Guid guidMajorType;
    public Guid guidSubtype;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputStreamInfo
{
    public uint dwFlags;
    public uint cbSize;
    public uint cbAlignment;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputDataBuffer
{
    public uint dwStreamID;
    public IntPtr pSample;
    public uint dwStatus;
    public IntPtr pEvents;
}

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare();
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    void GetDouble(); void GetGUID();
    [PreserveSig] int GetStringLength(in Guid key, out uint length);
    [PreserveSig] int GetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder value, uint size, out uint length);
    void GetAllocatedString();
    [PreserveSig] int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] int GetBlob(in Guid key, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buf, uint bufSize, out uint size);
    void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
    void SetUINT32(in Guid key, uint value);
    void SetUINT64(in Guid key, ulong value);
    void SetDouble();
    void SetGUID(in Guid key, in Guid value);
    void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
}

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare();
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
    [PreserveSig] int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] int GetBlob(in Guid key, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buf, uint bufSize, out uint size);
    void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
    void SetUINT32(in Guid key, uint value);
    void SetUINT64(in Guid key, ulong value);
    void SetDouble();
    void SetGUID(in Guid key, in Guid value);
    void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    // IMFMediaType
    void GetMajorType(); void IsCompressedFormat(); void IsEqual(); void GetRepresentation(); void FreeRepresentation();
}

[ComImport, Guid("7FEE9E9A-4A89-47a6-899C-B6A53A70FB67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFActivate
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare();
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    void GetUINT64(); void GetDouble(); void GetGUID();
    [PreserveSig] int GetStringLength(in Guid key, out uint length);
    [PreserveSig] int GetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder value, uint size, out uint length);
    void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
    void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    // IMFActivate
    [PreserveSig] int ActivateObject(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    [PreserveSig] int ShutdownObject();
    void DetachObject();
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare();
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    void GetUINT64(); void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
    void SetUINT32(in Guid key, uint value);
    void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    // IMFSample
    void GetSampleFlags(); void SetSampleFlags();
    void GetSampleTime(out long time);
    void SetSampleTime(long time);
    void GetSampleDuration(out long duration);
    void SetSampleDuration(long duration);
    void GetBufferCount(out uint count);
    void GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    void AddBuffer(IMFMediaBuffer buffer);
    void RemoveBufferByIndex(uint index);
    void RemoveAllBuffers();
    void GetTotalLength(out uint length);
    void CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    void Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
    void Unlock();
    void GetCurrentLength(out uint length);
    void SetCurrentLength(uint length);
    void GetMaxLength(out uint length);
}

[ComImport, Guid("DF598932-F10C-4E39-BBA2-C308F101DAA3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEvent
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64(); void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
    void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    // IMFMediaEvent
    void GetType(out uint type);
    void GetExtendedType(out Guid type);
    void GetStatus(out int status);
    void GetValue();
}

[ComImport, Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEventGenerator
{
    [PreserveSig] int GetEvent(uint flags, out IMFMediaEvent evt);
    void BeginGetEvent(); void EndGetEvent(); void QueueEvent();
}

[ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFTransform
{
    void GetStreamLimits(out uint inMin, out uint inMax, out uint outMin, out uint outMax);
    void GetStreamCount(out uint inputs, out uint outputs);
    [PreserveSig] int GetStreamIDs(uint inSize, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] uint[] inIds, uint outSize, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] uint[] outIds);
    void GetInputStreamInfo();
    void GetOutputStreamInfo(uint stream, out MftOutputStreamInfo info);
    [PreserveSig] int GetAttributes(out IMFAttributes attributes);
    void GetInputStreamAttributes(); void GetOutputStreamAttributes(); void DeleteInputStream(); void AddInputStreams();
    [PreserveSig] int GetInputAvailableType(uint stream, uint index, out IMFMediaType type);
    [PreserveSig] int GetOutputAvailableType(uint stream, uint index, out IMFMediaType type);
    [PreserveSig] int SetInputType(uint stream, IMFMediaType? type, uint flags);
    [PreserveSig] int SetOutputType(uint stream, IMFMediaType? type, uint flags);
    void GetInputCurrentType();
    [PreserveSig] int GetOutputCurrentType(uint stream, out IMFMediaType type);
    void GetInputStatus(); void GetOutputStatus(); void SetOutputBounds(); void ProcessEvent();
    [PreserveSig] int ProcessMessage(int message, IntPtr param);
    [PreserveSig] int ProcessInput(uint stream, IMFSample sample, uint flags);
    [PreserveSig] int ProcessOutput(uint flags, uint count, ref MftOutputDataBuffer buffers, out uint status);
}

[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(in Guid api);
    [PreserveSig] int IsModifiable(in Guid api);
    void GetParameterRange(); void GetParameterValues(); void GetDefaultValue();
    [PreserveSig] int GetValue(in Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig] int SetValue(in Guid api, [MarshalAs(UnmanagedType.Struct)] ref object value);
}

internal static class MfNative
{
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFTEnumEx(Guid category, uint flags, in MftRegisterTypeInfo input, in MftRegisterTypeInfo output, out IntPtr activates, out uint count);

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new MediaFoundationException($"{what} failed (0x{hr:X8})", hr);
    }

    private static int _startCount;
    private static readonly Lock StartLock = new();

    public static void EnsureStarted()
    {
        lock (StartLock)
        {
            if (_startCount++ == 0) Check(MFStartup(MfConst.MF_VERSION, 0), "MFStartup");
        }
    }
}
