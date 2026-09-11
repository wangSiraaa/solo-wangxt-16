namespace AdRecon.Api.Domain;

public enum ContractStatus
{
    Active = 0,
    Completed = 1
}

public enum BatchStatus
{
    /// <summary>正常完成（可能含行级去重）。</summary>
    Completed = 0,

    /// <summary>整批重复：相同 BatchKey 的批次已存在，本次重传被幂等吞掉。</summary>
    DuplicateBatch = 1
}

public enum MakeGoodStatus
{
    Active = 0,
    Cancelled = 1,
    Fulfilled = 2
}

public enum AllocationStatus
{
    /// <summary>占用中：这部分补量曝光正冲抵某个合同的缺口。</summary>
    Active = 0,

    /// <summary>已释放：计划撤销后归还资源池。</summary>
    Released = 1
}

public static class AdjustmentReasons
{
    /// <summary>迟到数据：落在已确认账期内，只能记差额，不能改台账。</summary>
    public const string LateArrival = "LateArrival";
}
