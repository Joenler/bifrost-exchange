namespace Bifrost.Contracts.Internal.McLog;

/// <summary>
/// Wire DTO for publications on bifrost.mc.v1/mc.command.{cmd_snake}. The
/// recorder binds mc.command.# and writes into the mc_commands table. ArgsJson
/// is the protobuf-JSON representation of the originating McCommand;
/// NewStateJson is the protobuf-JSON of McCommandResult.new_state for accepted
/// commands and the empty string for rejections. Rejected commands are still
/// audit-logged: Success=false + Message carries rejection detail +
/// NewStateJson="".
/// </summary>
public sealed record McCommandLogPayload(
    long TimestampNs,
    string Command,
    string ArgsJson,
    bool Success,
    string Message,
    string NewStateJson,
    string OperatorHostname);
