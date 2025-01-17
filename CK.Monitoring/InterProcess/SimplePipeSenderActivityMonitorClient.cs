using CK.Core;
using System;
using System.Collections.Generic;
using System.IO.Pipes;

namespace CK.Monitoring.InterProcess;

/// <summary>
/// Simple activity monitor client that uses a <see cref="AnonymousPipeClientStream"/> to sends log
/// entries to a <see cref="SimpleLogPipeReceiver"/>.
/// </summary>
public sealed class SimplePipeSenderActivityMonitorClient : IActivityMonitorClient, IDisposable
{
    readonly CKBinaryWriter _writer;
    readonly AnonymousPipeClientStream _client;
    bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="SimplePipeSenderActivityMonitorClient"/>.
    /// </summary>
    /// <param name="pipeHandlerName">The name of the server pipe.</param>
    public SimplePipeSenderActivityMonitorClient( string pipeHandlerName )
    {
        _client = new AnonymousPipeClientStream( PipeDirection.Out, pipeHandlerName );
        _writer = new CKBinaryWriter( _client );
        _writer.Write( LogReader.CurrentStreamVersion );
    }

    /// <summary>
    /// Sends a goodbye message (a zero byte) and closes this side of the pipe.
    /// </summary>
    public void Dispose()
    {
        if( !_disposed )
        {
            _disposed = true;
            _client.WriteByte( 0 );
            _writer.Dispose();
            _client.Dispose();
        }
    }

    void IActivityMonitorClient.OnAutoTagsChanged( CKTrait newTrait )
    {
    }

    void IActivityMonitorClient.OnGroupClosed( IActivityLogGroup group, IReadOnlyList<ActivityLogGroupConclusion>? conclusions )
    {
        LogEntry.WriteCloseGroup( _writer, group.Data.Level, group.CloseLogTime, conclusions );
    }

    void IActivityMonitorClient.OnGroupClosing( IActivityLogGroup group, ref List<ActivityLogGroupConclusion>? conclusions )
    {
    }

    void IActivityMonitorClient.OnOpenGroup( IActivityLogGroup group )
    {
        LogEntry.WriteLog( _writer, true, group.Data.Level, group.Data.LogTime, group.Data.Text, group.Data.Tags, group.Data.ExceptionData, group.Data.FileName, group.Data.LineNumber );
    }

    void IActivityMonitorClient.OnTopicChanged( string newTopic, string? fileName, int lineNumber )
    {
    }

    void IActivityMonitorClient.OnUnfilteredLog( ref ActivityMonitorLogData data )
    {
        LogEntry.WriteLog( _writer, false, data.Level, data.LogTime, data.Text, data.Tags, data.ExceptionData, data.FileName, data.LineNumber );
    }
}
