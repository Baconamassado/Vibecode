using System.Runtime.InteropServices;

namespace Bakobtify.Playback;

// Constantes e GUIDs do Media Foundation usados pelo decoder.
internal static class MfGuids
{
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    public static readonly Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
    public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
    public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");
    public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322DE230-9EEB-43BD-AB7A-FF412251541D");
    public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1AAB75C8-CFEF-451C-AB95-AC034B8E1731");
    public static readonly Guid MF_PD_DURATION = new("6C990D33-BB8E-477A-8598-0D5D96FCD88A");

    public static readonly Guid MFAudioFormat_Audio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00AA00389B71");

    public const uint MF_VERSION = 0x00020070;
    public const uint MFSTARTUP_FULL = 0;

    public const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;
    public const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    public const uint MF_SOURCE_READER_MEDIASOURCE = 0xFFFFFFFF;

    public const uint MF_SOURCE_READERF_ERROR = 0x00000001;
    public const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;
    public const uint MF_SOURCE_READERF_STREAMTICK = 0x00000100;

    public const ushort VT_I8 = 20;
    public const ushort VT_UI8 = 21;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public long hVal;
    public long padding;
}

[ComImport]
[Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(in Guid guidKey, IntPtr pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(in Guid guidKey, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int Compare(IMFAttributes pTheirs, uint matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(in Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, byte[] pBuf, uint cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint pcItems);
    [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);
}

[ComImport]
[Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    [PreserveSig] int GetMajorType(out Guid pguidMajorType);
    [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
    [PreserveSig] int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
    [PreserveSig] int GetRepresentation(in Guid guidRepresentation, out IntPtr ppvRepresentation);
    [PreserveSig] int FreeRepresentation(in Guid guidRepresentation, IntPtr pvRepresentation);
}

[ComImport]
[Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
    [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
    [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
}

[ComImport]
[Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample : IMFAttributes
{
    // A vtable nativa de IMFSample começa com as 30 entradas de IMFAttributes.
    [PreserveSig] new int GetItem(in Guid guidKey, IntPtr pValue);
    [PreserveSig] new int GetItemType(in Guid guidKey, out int pType);
    [PreserveSig] new int CompareItem(in Guid guidKey, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] new int Compare(IMFAttributes pTheirs, uint matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] new int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig] new int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig] new int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] new int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig] new int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig] new int GetString(in Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] new int GetAllocatedString(in Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
    [PreserveSig] new int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] new int GetBlob(in Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] new int GetAllocatedBlob(in Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    [PreserveSig] new int GetUnknown(in Guid guidKey, in Guid riid, out IntPtr ppv);
    [PreserveSig] new int SetItem(in Guid guidKey, IntPtr value);
    [PreserveSig] new int DeleteItem(in Guid guidKey);
    [PreserveSig] new int DeleteAllItems();
    [PreserveSig] new int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig] new int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig] new int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] new int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] new int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] new int SetBlob(in Guid guidKey, byte[] pBuf, uint cbBufSize);
    [PreserveSig] new int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig] new int LockStore();
    [PreserveSig] new int UnlockStore();
    [PreserveSig] new int GetCount(out uint pcItems);
    [PreserveSig] new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    [PreserveSig] new int CopyAllItems(IMFAttributes pDest);

    [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
    [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
    [PreserveSig] int GetSampleTime(out long phnsSampleTime);
    [PreserveSig] int SetSampleTime(long hnsSampleTime);
    [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
    [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
    [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
    [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
    [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
}

[ComImport]
[Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] out bool pfSelected);
    [PreserveSig] int SetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);
    [PreserveSig] int GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, out IMFMediaType ppMediaType);
    [PreserveSig] int GetCurrentMediaType(uint dwStreamIndex, out IMFMediaType ppMediaType);
    [PreserveSig] int SetCurrentMediaType(uint dwStreamIndex, IntPtr pdwReserved, IMFMediaType pMediaType);
    [PreserveSig] int SetCurrentPosition(in Guid guidTimeFormat, in PropVariant varPosition);
    [PreserveSig] int ReadSample(
        uint dwStreamIndex,
        uint dwControlFlags,
        out uint pdwActualStreamIndex,
        out uint pdwStreamFlags,
        out long pllTimestamp,
        out IMFSample ppSample);
    [PreserveSig] int Flush(uint dwStreamIndex);
    [PreserveSig] int GetServiceForStream(uint dwStreamIndex, in Guid guidService, in Guid riid, out IntPtr ppvObject);
    [PreserveSig] int GetPresentationAttribute(uint dwStreamIndex, in Guid guidAttribute, out PropVariant pvarAttribute);
}

internal static class MfInterop
{
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszURL,
        IMFAttributes? pAttributes,
        out IMFSourceReader ppSourceReader);

    private static readonly object StartLock = new();
    private static bool _started;

    public static void EnsureStarted()
    {
        lock (StartLock)
        {
            if (_started) return;
            var hr = MFStartup(MfGuids.MF_VERSION, MfGuids.MFSTARTUP_FULL);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            _started = true;
        }
    }

    public static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }
}
