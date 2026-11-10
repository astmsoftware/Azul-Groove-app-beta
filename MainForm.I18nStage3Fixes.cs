namespace AzulGroove;

public partial class MainForm
{
    static readonly Dictionary<string, string> I18nRawStage3Fixes = new()
    {
        ["nl"] = """{"off.openChat":"Offlinechat","s3.actLive":"Live-uitzending"}""",
        ["de"] = """{"s3.actLive":"Live-Auftritt"}""",
        ["hi"] = """{"s3.rpcUiWs":"WebSocket लिंक"}""",
        ["ja"] = """{"s3.rpcUiWs":"WebSocket の URL"}""",
        ["ko"] = """{"s3.rpcUiWs":"WebSocket 링크"}"""
    };
}
