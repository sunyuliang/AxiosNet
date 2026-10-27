namespace AxiosNet;

/// <summary>进度事件，对应 axios 的 <c>progressEvent</c>（upload / download 各一套）。</summary>
public readonly struct AxiosProgressEvent
{
    public AxiosProgressEvent(long loaded, long? total, double? rate, TimeSpan? estimated, bool upload)
    {
        Loaded = loaded;
        Total = total;
        Rate = rate;
        Estimated = estimated;
        Upload = upload;
    }

    /// <summary>已传输字节数。</summary>
    public long Loaded { get; }

    /// <summary>总字节数；服务端未声明长度时为 null。</summary>
    public long? Total { get; }

    /// <summary>完成比例 0~1；总长度未知时为 null。</summary>
    public double? Progress => Total is > 0 ? Math.Min(1d, (double)Loaded / Total.Value) : null;

    /// <summary>瞬时平均速率（字节/秒）。</summary>
    public double? Rate { get; }

    /// <summary>按当前速率估算的剩余时间。</summary>
    public TimeSpan? Estimated { get; }

    /// <summary>true = 上传进度，false = 下载进度。</summary>
    public bool Upload { get; }

    public override string ToString()
    {
        var percent = Progress is double p ? $"{p * 100:0.0}%" : "?%";
        return $"{(Upload ? "upload" : "download")} {Loaded}/{Total?.ToString() ?? "?"} bytes ({percent})";
    }
}
