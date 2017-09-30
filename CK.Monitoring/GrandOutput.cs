using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CK.Core;
using CK.Monitoring.Impl;
using System.Linq;
using System.Reflection;

namespace CK.Monitoring
{
    /// <summary>
    /// A GrandOutput collects activity of multiple <see cref="IActivityMonitor"/>. 
    /// It is usually useless to explicitly create an instance of GrandOutput: the <see cref="Default"/> one is 
    /// available as soon as <see cref="EnsureActiveDefault"/> is called 
    /// and will be automatically used by new <see cref="ActivityMonitor"/>.
    /// </summary>
    public sealed partial class GrandOutput : IDisposable
    {
        readonly List<WeakReference<GrandOutputClient>> _clients;
        readonly DispatcherSink _sink;
        readonly object _externalLogLock;
        DateTimeStamp _externalLogLastTime;
        int _handleCriticalErrors;


        static GrandOutput _default;
        static readonly object _defaultLock = new object();

        /// <summary>
        /// The tag that marks all external log entry sent when <see cref="HandleCriticalErrors"/>
        /// is true.
        /// </summary>
        public static CKTrait CriticalErrorTag = ActivityMonitor.Tags.Context.FindOrCreate( "CriticalError" );

        /// <summary>
        /// Gets the default <see cref="GrandOutput"/> for the current Application Domain.
        /// Note that <see cref="EnsureActiveDefault"/> must have been called, otherwise this static property is null
        /// and that this Default can be <see cref="Dispose()"/> at any time (this static property will be set back to null).
        /// </summary>
        public static GrandOutput Default => _default;

        /// <summary>
        /// Ensures that the <see cref="Default"/> GrandOutput is created and that any <see cref="ActivityMonitor"/> that will be created in this
        /// application domain will automatically have a <see cref="GrandOutputClient"/> registered for this Default GrandOutput.
        /// If the Default is already initialized, the <paramref name="configuration"/> is applied.
        /// </summary>
        /// <param name="configuration">
        /// Configuration to apply to the default GrandOutput.
        /// When null, a default configuration with a <see cref="Handlers.TextFileConfiguration"/> in a "Text" path is configured.
        /// </param>
        /// <returns>The Default GrandOutput.</returns>
        /// <remarks>
        /// <para>
        /// This method is thread-safe (a simple lock protects it) and uses a <see cref="ActivityMonitor.AutoConfiguration"/> action 
        /// that uses <see cref="EnsureGrandOutputClient(IActivityMonitor)"/> on newly created ActivityMonitor.
        /// </para>
        /// <para>
        /// The Default GrandOutput can safely be <see cref="Dispose()"/> at any time: disposing the Default 
        /// sets it to null.
        /// </para>
        /// </remarks>
        static public GrandOutput EnsureActiveDefault( GrandOutputConfiguration configuration )
        {
            lock( _defaultLock )
            {
                if( _default == null )
                {
                    bool ensureStaticIntialization = LogFile.TrackActivityMonitorLoggingError;
                    if( configuration == null )
                    {
                        configuration = new GrandOutputConfiguration()
                                            .AddHandler( new Handlers.TextFileConfiguration() { Path = "Text" } );
                        configuration.InternalClone = true;
                    }
                    _default = new GrandOutput( configuration, true );
                    ActivityMonitor.AutoConfiguration += AutoRegisterDefault;
                }
                else if( configuration != null ) _default.ApplyConfiguration( configuration, true );
            }
            return _default;
        }

        static void AutoRegisterDefault( IActivityMonitor m )
        {
            Default?.EnsureGrandOutputClient( m );
        }

        /// <summary>
        /// Applies a configuration.
        /// This is thread safe and can be called at any moment.
        /// </summary>
        /// <param name="configuration">The configuration to apply.</param>
        /// <param name="waitForApplication">
        /// True to block until this configuration has been applied.
        /// Note that another (new) configuration may have already replaced the given configuration
        /// once this call ends.
        /// </param>
        public void ApplyConfiguration( GrandOutputConfiguration configuration, bool waitForApplication = false )
        {
            if( configuration == null ) throw new ArgumentNullException( nameof( configuration ) );
            if( !configuration.InternalClone )
            {
                configuration = configuration.Clone();
                configuration.InternalClone = true;
            }
            _sink.ApplyConfiguration( configuration, waitForApplication );
        }

        /// <summary>
        /// Settable factory method for <see cref="IGrandOutputHandler"/>.
        /// Default implementation relies on Handlers that must be in the same 
        /// assembly and namespace as their configuration objects and named the 
        /// same without the "Configuration" suffix.
        /// </summary>
        static public Func<IHandlerConfiguration, IGrandOutputHandler> CreateHandler = config =>
         {
             string name = config.GetType().GetTypeInfo().FullName;
             if( !name.EndsWith( "Configuration" ) ) throw new Exception( $"Configuration handler type name must end with 'Configuration': {name}." );
             name = config.GetType().AssemblyQualifiedName.Replace( "Configuration,", "," );
             Type t = Type.GetType( name, throwOnError: true );
             return (IGrandOutputHandler)Activator.CreateInstance( t, new[] { config } );
         };

        /// <summary>
        /// Initializes a new <see cref="GrandOutput"/>. 
        /// </summary>
        /// <param name="config">The configuration.</param>
        /// <param name="handleCriticalErrors">True to handle critical errors.</param>
        public GrandOutput( GrandOutputConfiguration config, bool handleCriticalErrors = false )
        {
            if( config == null ) throw new ArgumentNullException( nameof( config ) );
            // Creates the client list first.
            _clients = new List<WeakReference<GrandOutputClient>>();
            // Starts the pump thread. Its monitor will be registered
            // in this GrandOutput.
            _sink = new DispatcherSink( m => DoEnsureGrandOutputClient( m ), config.TimerDuration, TimeSpan.FromMinutes( 5 ), DoGarbageDeadClients );
            _externalLogLock = new object();
            _externalLogLastTime = DateTimeStamp.MinValue;
            HandleCriticalErrors = handleCriticalErrors;
            ApplyConfiguration( config, waitForApplication: true );
        }

        /// <summary>
        /// Ensures that a client for this GrandOutput is registered on a monitor.
        /// There is no need to call this method for the <see cref="Default"/> GrandOutput since
        /// clients are automatically registered for newly created <see cref="ActivityMonitor"/> (thanks
        /// to a <see cref="ActivityMonitor.AutoConfiguration"/> hook).
        /// </summary>
        /// <param name="monitor">The monitor onto which a <see cref="GrandOutputClient"/> must be registered.</param>
        /// <returns>A newly created client or the already existing one.</returns>
        public GrandOutputClient EnsureGrandOutputClient( IActivityMonitor monitor )
        {
            if( IsDisposed ) throw new ObjectDisposedException( nameof( GrandOutput ) );
            if( monitor == null ) throw new ArgumentNullException( nameof( monitor ) );
            return DoEnsureGrandOutputClient( monitor );
        }

        GrandOutputClient DoEnsureGrandOutputClient( IActivityMonitor monitor )
        {
            Func<GrandOutputClient> reg = () =>
            {
                var c = new GrandOutputClient( this );
                lock( _clients )
                {
                    if( IsDisposed ) c = null;
                    else _clients.Add( new WeakReference<GrandOutputClient>( c ) );
                }
                return c;
            };
            return monitor.Output.RegisterUniqueClient( b => b.Central == this, reg );
        }

        /// <summary>
        /// Gets or sets the filter for ExternalLog methods.
        /// Defaults to <see cref="LogLevelFilter.None"/>: <see cref="ActivityMonitor.DefaultFilter"/>.<see cref="LogFilter.Line">Line</see>
        /// is used.
        /// </summary>
        public LogLevelFilter ExternalLogFilter { get; set; }

        /// <summary>
        /// Gets whether a log level should be emitted.
        /// We consider that as long has the log level has <see cref="CK.Core.LogLevel.IsFiltered">IsFiltered</see>
        /// bit set, the decision has already being taken and we return true.
        /// But for logs that do not claim to have been filtered, we challenge the <see cref="ExternalLogFilter"/>
        /// (and if it is <see cref="LogLevelFilter.None"/>, the static <see cref="ActivityMonitor.DefaultFilter"/>).
        /// </summary>
        /// <param name="level">Log level to test.</param>
        /// <returns>True if this level should be logged otherwise false.</returns>
        public bool IsExternalLogEnabled( LogLevel level )
        {
            LogLevelFilter filter = ExternalLogFilter;
            if( filter == LogLevelFilter.None ) filter = ActivityMonitor.DefaultFilter.Line;
            if( (level & LogLevel.IsFiltered) == 0 )
            {
                if( (int)filter > (int)(level & LogLevel.Mask) ) return true;
            }
            return false;
        }

        /// <summary>
        /// Logs an entry from any contextless source.
        /// The monitor target has <see cref="Guid.Empty"/> as its <see cref="ActivityMonitor.UniqueId"/>.
        /// </summary>
        /// <remarks>
        /// We consider that as long has the log level has <see cref="CK.Core.LogLevel.IsFiltered">IsFiltered</see> bit
        /// set, the decision has already being taken and here we do our job: dispatching of the log.
        /// But for logs that do not claim to have been filtered, we challenge the <see cref="ExternalLogFilter"/>.
        /// </remarks>
        /// <param name="level">Log level.</param>
        /// <param name="message">String message.</param>
        /// <param name="ex">Optional exception.</param>
        /// <param name="tags">Optional tags (that must belong to <see cref="ActivityMonitor.Tags.Context"/>).</param>
        public void ExternalLog( LogLevel level, string message, Exception ex = null, CKTrait tags = null )
        {
            LogLevelFilter filter = ExternalLogFilter;
            if( filter == LogLevelFilter.None ) filter = ActivityMonitor.DefaultFilter.Line;
            if( (level & LogLevel.IsFiltered) == 0 )
            {
                if( (int)filter > (int)(level & LogLevel.Mask) ) return;
            }
            DateTimeStamp prevLogTime;
            DateTimeStamp logTime;
            lock( _externalLogLock )
            {
                prevLogTime = _externalLogLastTime;
                _externalLogLastTime = logTime = new DateTimeStamp( _externalLogLastTime, DateTime.UtcNow );
            }
            var e = LogEntry.CreateMulticastLog(
                        Guid.Empty,
                        LogEntryType.Line,
                        prevLogTime,
                        depth: 0,
                        text: string.IsNullOrEmpty( message ) ? ActivityMonitor.NoLogText : message,
                        t: logTime,
                        level: level,
                        fileName: null,
                        lineNumber: 0,
                        tags: tags,
                        ex: ex != null ? CKExceptionData.CreateFrom( ex ) : null );
            _sink.Handle( new GrandOutputEventInfo( e, String.Empty ) );
        }

        /// <summary>
        /// Logs an entry from any contextless source.
        /// The monitor target has <see cref="Guid.Empty"/> as its <see cref="ActivityMonitor.UniqueId"/>.
        /// </summary>
        /// <remarks>
        /// We consider that as long has the log level has <see cref="CK.Core.LogLevel.IsFiltered">IsFiltered</see>
        /// bit set, the decision has already being taken and here we do our job: dispatching of the log.
        /// But for logs that do not claim to have been filtered, we challenge the <see cref="ExternalLogFilter"/>.
        /// </remarks>
        /// <param name="level">Log level.</param>
        /// <param name="message">String message.</param>
        /// <param name="tags">Optional tags (that must belong to <see cref="ActivityMonitor.Tags.Context"/>).</param>
        public void ExternalLog( LogLevel level, string message, CKTrait tags ) => ExternalLog( level, message, null, tags );

        /// <summary>
        /// Gets a cancellation token that is cancelled at the start
        /// of <see cref="Dispose()"/>.
        /// </summary>
        public CancellationToken DisposingToken => _sink.StoppingToken;

        /// <summary>
        /// Gets the sink.
        /// </summary>
        public IGrandOutputSink Sink => _sink;

        /// <summary>
        /// Gets or sets whether this GrandOutput subscribes to <see cref="ActivityMonitor.CriticalErrorCollector"/>
        /// events and sends them by calling <see cref="ExternalLog(CK.Core.LogLevel, string, Exception, CKTrait)">ExternalLog</see>
        /// with a <see cref="CriticalErrorTag"/> tag.
        /// Defaults to true for the <see cref="Default"/> GrandOutput, false otherwise.
        /// </summary>
        public bool HandleCriticalErrors
        {
            get { return _handleCriticalErrors != 0; }
            set
            {
                if( value )
                {
                    if( Interlocked.Exchange( ref _handleCriticalErrors, 1 ) == 0 )
                    {
                        ActivityMonitor.CriticalErrorCollector.OnErrorFromBackgroundThreads += CriticalErrorCollector_OnErrorFromBackgroundThreads;
                    }
                }
                else
                {
                    if( Interlocked.Exchange( ref _handleCriticalErrors, 0 ) == 1 )
                    {
                        ActivityMonitor.CriticalErrorCollector.OnErrorFromBackgroundThreads -= CriticalErrorCollector_OnErrorFromBackgroundThreads;
                    }
                }
            }
        }

        void CriticalErrorCollector_OnErrorFromBackgroundThreads( object sender, CriticalErrorCollector.ErrorEventArgs e )
        {
            int c = e.Errors.Count;
            while( --c >= 0 )
            {
                var err = e.Errors[c];
                ExternalLog( LogLevel.Fatal, err.Comment, err.Exception, CriticalErrorTag );
            }
        }

        void DoGarbageDeadClients()
        {
            lock( _clients )
            {
                for( int i = 0; i < _clients.Count; ++i )
                {
                    GrandOutputClient cw;
                    if( !_clients[i].TryGetTarget( out cw ) || !cw.IsBoundToMonitor )
                    {
                        _clients.RemoveAt( i-- );
                    }
                }
            }
        }


        /// <summary>
        /// Gets whether this GrandOutput has been disposed.
        /// </summary>
        public bool IsDisposed => !_sink.IsRunning;

        /// <summary>
        /// Closes this <see cref="GrandOutput"/>.
        /// If this is the default one that is disposed, <see cref="Default"/> is set to null.
        /// </summary>
        /// <param name="millisecondsBeforeForceClose">Maximal time to wait.</param>
        public void Dispose( int millisecondsBeforeForceClose = Timeout.Infinite )
        {
            if( _sink.Stop() )
            {
                HandleCriticalErrors = false;
                lock( _defaultLock )
                {
                    if( _default == this )
                    {
                        ActivityMonitor.AutoConfiguration -= AutoRegisterDefault;
                        _default = null;
                    }
                }
                lock( _clients )
                {
                    for( int i = 0; i < _clients.Count; ++i )
                    {
                        GrandOutputClient cw;
                        if( _clients[i].TryGetTarget( out cw ) && cw.IsBoundToMonitor )
                        {
                            cw.OnCentralDisposed();
                        }
                    }
                }
                _sink.Finalize( millisecondsBeforeForceClose );
            }
        }

        /// <summary>
        /// Calls <see cref="Dispose(int)"/> with <see cref="Timeout.Infinite"/>.
        /// If this is the default one that is disposed, <see cref="Default"/> is set to null.
        /// </summary>
        public void Dispose()
        {
            Dispose( Timeout.Infinite );
        }


    }
}
