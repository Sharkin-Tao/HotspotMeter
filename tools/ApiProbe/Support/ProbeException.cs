namespace HotspotMeter.ApiProbe.Support;

/// <summary>原型内部的可预期错误：带清晰中文说明，直接打印给使用者，不打印堆栈。</summary>
internal sealed class ProbeException : Exception
{
    public ProbeException(string message)
        : base(message)
    {
    }
}