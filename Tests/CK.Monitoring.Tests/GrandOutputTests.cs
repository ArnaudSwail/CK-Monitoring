using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CK.Core;
using NUnit.Framework;
using FluentAssertions;
using System.Threading;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace CK.Monitoring.Tests;

[TestFixture]
public class GrandOutputTests
{
    static readonly Exception _exception1;
    static readonly Exception _exception2;

    // Uses static initialization once for all.
    // On netcoreapp1.1, seems that throw/catch has heavy performance issues.
    static GrandOutputTests()
    {
        try
        {
            throw new InvalidOperationException( "Exception!" );
        }
        catch( Exception e )
        {
            _exception1 = e;
        }

        try
        {
            throw new InvalidOperationException( "Inception!", _exception1 );
        }
        catch( Exception e )
        {
            _exception2 = e;
        }
    }

    [SetUp]
    public void InitalizePaths()
    {
        TestHelper.InitalizePaths();
        TestHelper.PrepareLogFolder( "Gzip" );
        TestHelper.PrepareLogFolder( "Termination" );
        TestHelper.PrepareLogFolder( "TerminationLost" );
        TestHelper.WaitForNoMoreAliveInputLogEntry();
    }

    [TearDown]
    public void WaitForNoMoreAliveInputLogEntry()
    {
        TestHelper.WaitForNoMoreAliveInputLogEntry();
    }

    [Explicit]
    [Test]
    public async Task Console_handler_demo_Async()
    {
        var a = new ActivityMonitor();
        a.Output.RegisterClient( new ActivityMonitorConsoleClient() );
        a.Info( "This is an ActivityMonitor Console demo." );
        LogDemo( a );
        var c = new GrandOutputConfiguration();
        c.AddHandler( new Handlers.ConsoleConfiguration() );
        c.AddHandler( new Handlers.TextFileConfiguration()
        {
            Path = "test"
        } );
        await using( var g = new GrandOutput( c ) )
        {
            var m = CreateMonitorAndRegisterGrandOutput( "Hello Console!", g );
            m.Info( "This is the same demo, but with the GrandOutputConsole." );
            LogDemo( m );
        }
    }

    void LogDemo( IActivityMonitor m )
    {
        m.Info( "This is an info." );
        using( m.OpenInfo( $"This is an info group." ) )
        {
            m.Fatal( $"Ouch! a faaaaatal." );
            m.OpenTrace( $"A trace" );
            var group = m.OpenInfo( $"This is another group (trace)." );
            {
                try
                {
                    throw new Exception();
                }
                catch( Exception ex )
                {
                    m.Error( "An error occurred.", ex );
                }
            }
            m.CloseGroup( "This is a close group." );
            group.Dispose();
        }
    }

    [Test]
    public async Task reading_static_logger_entries_Async()
    {
        string folder = TestHelper.PrepareLogFolder( "StaticLogger" );

        var c = new GrandOutputConfiguration()
        {
            Handlers = {
                new Handlers.BinaryFileConfiguration { Path = folder + "/Raw" },
                new Handlers.BinaryFileConfiguration { Path = folder + "/GZip", UseGzipCompression = true } }
        };

        await using( GrandOutput g = new GrandOutput( c ) )
        {
            ActivityMonitor.StaticLogger.Info( "This is a static log." );
        }
        string ckmon = TestHelper.WaitForCkmonFilesInDirectory( folder + "/Raw", 1 ).Single();
        ReadLog( ckmon );
        string gzip = TestHelper.WaitForCkmonFilesInDirectory( folder + "/GZip", 1 ).Single();
        ReadLog( gzip );

        static void ReadLog( string ckmon )
        {
            using var r = LogReader.Open( ckmon );
            r.BadEndOfFileMarker.Should().BeFalse();
            r.ReadException.Should().BeNull();
            bool d = false;
            while( r.MoveNext() )
            {
                Debug.Assert( r.CurrentMulticast != null );
                if( r.CurrentMulticast.MonitorId == ActivityMonitor.StaticLogMonitorUniqueId )
                {
                    r.CurrentMulticast.FileName.Should().EndWith( "GrandOutputTests.cs" );
                    r.CurrentMulticast.Text.Should().Be( "This is a static log." );
                    d = true;
                }
            }
            r.ReadException.Should().BeNull();
            d.Should().BeTrue();
        }
    }

    [Test]
    [Explicit( "Buggy. To be fixed." )]
    public async Task CKMon_binary_files_can_be_GZip_compressed_Async()
    {
        string folder = TestHelper.PrepareLogFolder( "Gzip" );

        var c = new GrandOutputConfiguration()
                        .AddHandler( new Handlers.BinaryFileConfiguration()
                        {
                            Path = folder + @"\OutputGzip",
                            UseGzipCompression = true
                        } )
                        .AddHandler( new Handlers.BinaryFileConfiguration()
                        {
                            Path = folder + @"\OutputRaw",
                            UseGzipCompression = false
                        } );

        await using( GrandOutput g = new GrandOutput( c ) )
        {
            var taskA = Task.Factory.StartNew( () => DumpMonitor1083Entries( CreateMonitorAndRegisterGrandOutput( "Task A", g ), 5 ), default, TaskCreationOptions.LongRunning, TaskScheduler.Default );
            var taskB = Task.Factory.StartNew( () => DumpMonitor1083Entries( CreateMonitorAndRegisterGrandOutput( "Task B", g ), 5 ), default, TaskCreationOptions.LongRunning, TaskScheduler.Default );
            var taskC = Task.Factory.StartNew( () => DumpMonitor1083Entries( CreateMonitorAndRegisterGrandOutput( "Task C", g ), 5 ), default, TaskCreationOptions.LongRunning, TaskScheduler.Default );

            await Task.WhenAll( taskA, taskB, taskC );
            await Task.Delay( 1000 );
        }

        string gzipCkmon = TestHelper.WaitForCkmonFilesInDirectory( folder + @"\OutputGzip", 1 ).Single();
        string rawCkmon = TestHelper.WaitForCkmonFilesInDirectory( folder + @"\OutputRaw", 1 ).Single();

        FileInfo gzipCkmonFile = new FileInfo( gzipCkmon );
        FileInfo rawCkmonFile = new FileInfo( rawCkmon );

        // Test file size
        gzipCkmonFile.Exists.Should().BeTrue();
        rawCkmonFile.Exists.Should().BeTrue();
        gzipCkmonFile.Length.Should().BeLessThan( rawCkmonFile.Length );

        // Check that the gzip file is the same as the raw file re-compressed.
        FileUtil.CompressFileToGzipFile( rawCkmon, rawCkmon + ".gz", false );
        File.ReadAllBytes( gzipCkmon ).SequenceEqual( File.ReadAllBytes( rawCkmon + ".gz" ) )
            .Should().BeTrue( $"File '{gzipCkmon}' must be the same as raw '{rawCkmon}' re-compressed." );


        // Check that all entries can be read from the gzip file.
        using( var r = LogReader.Open( gzipCkmon ) )
        {
            int count = 0;
            while( r.MoveNext() )
            {
                ++count;
            }
            Console.WriteLine( $"{count} entries successfully read from '{gzipCkmon}'." );
        }
        //
        using( var rG = new MultiLogReader() )
        using( var rR = new MultiLogReader() )
        {
            rG.Add( gzipCkmon, out var newFileIndexG );
            newFileIndexG.Should().BeTrue();
            rR.Add( rawCkmon, out var newFileIndexR );
            newFileIndexR.Should().BeTrue();
            var mapG = rG.GetActivityMap();
            var mapR = rR.GetActivityMap();

            mapG.Monitors.Count.Should().Be( mapR.Monitors.Count );
            mapG.FirstEntryDate.Should().Be( mapR.FirstEntryDate );
            mapG.LastEntryDate.Should().Be( mapR.LastEntryDate );
            for( var i = 1; i < mapG.Monitors.Count; ++i )
            {
                var monitorG = mapG.Monitors[i];
                var monitorR = mapR.Monitors[i];
                var monitorId = monitorR.MonitorId;
                monitorG.MonitorId.Should().Be( monitorId );
                monitorG.FirstEntryTime.Should().Be( monitorR.FirstEntryTime );
                monitorG.LastEntryTime.Should().Be( monitorR.LastEntryTime );
                monitorG.FirstDepth.Should().Be( monitorR.FirstDepth );
                monitorG.LastDepth.Should().Be( monitorR.LastDepth );
                monitorG.AllTags.Should().BeEquivalentTo( monitorR.AllTags );
                monitorG.Files.Should().HaveCount( 1 );
                monitorR.Files.Should().HaveCount( 1 );
                var firstOffset = monitorR.Files[0].FirstOffset;
                var lastOffset = monitorR.Files[0].LastOffset;
                monitorG.Files[0].FirstOffset.Should().Be( firstOffset );
                monitorG.Files[0].LastOffset.Should().Be( lastOffset );

                using( var fR = monitorR.Files[0].CreateFilteredReaderAndMoveTo( firstOffset ) )
                {
                    Debug.Assert( fR.CurrentMulticast != null );
                    fR.CurrentMulticast.MonitorId.Should().Be( monitorId );
                    fR.MoveNext().Should().BeTrue();
                }
                using( var lR = monitorR.Files[0].CreateFilteredReaderAndMoveTo( lastOffset ) )
                {
                    Debug.Assert( lR.CurrentMulticast != null );
                    lR.CurrentMulticast.MonitorId.Should().Be( monitorId );
                    FluentActions.Invoking( () => lR.MoveNext() ).Should().NotThrow();
                }
                using( var fG = monitorG.Files[0].CreateFilteredReaderAndMoveTo( firstOffset ) )
                {
                    Debug.Assert( fG.CurrentMulticast != null );
                    fG.CurrentMulticast.MonitorId.Should().Be( monitorId );
                    fG.MoveNext().Should().BeTrue();
                }
                using( var lG = monitorG.Files[0].CreateFilteredReaderAndMoveTo( lastOffset ) )
                {
                    Debug.Assert( lG.CurrentMulticast != null );
                    lG.CurrentMulticast.MonitorId.Should().Be( monitorId );
                    FluentActions.Invoking( () => lG.MoveNext() ).Should().NotThrow();
                }
            }
        }
        // Test de-duplication between Gzip and non-Gzip
        using( var mlr = new MultiLogReader() )
        {
            var fileList = mlr.Add( new string[] { gzipCkmonFile.FullName, rawCkmonFile.FullName } );
            fileList.Should().HaveCount( 2 );

            var map = mlr.GetActivityMap();

            map.Monitors.Count.Should().BeInRange( 4, 5 );
            if( map.Monitors.Count == 5 )
            {
                map.Monitors.Any( m => m.MonitorId == ActivityMonitor.StaticLogMonitorUniqueId ).Should().BeTrue( "The 5th monitor is the §§§§ (static) monitor." );
            }
            // The DispatcherSink monitor define its Topic: "CK.Monitoring.DispatcherSink"
            // Others do not have any topic.
            var notDispatcherSinkMonitors = map.Monitors.Where( m => m.MonitorId != ActivityMonitor.ExternalLogMonitorUniqueId
                                                                     && !m.AllTags.Any( t => t.Key == ActivityMonitor.Tags.TopicChanged ) ).ToList();
            using( var p = notDispatcherSinkMonitors.ElementAt( 0 ).ReadFirstPage( 6000 ) )
            {
                p.Entries.Should().HaveCount( 5425 );
            }
            using( var p = notDispatcherSinkMonitors.ElementAt( 1 ).ReadFirstPage( 6000 ) )
            {
                p.Entries.Should().HaveCount( 5425 );
            }
            using( var p = notDispatcherSinkMonitors.ElementAt( 2 ).ReadFirstPage( 6000 ) )
            {
                p.Entries.Should().HaveCount( 5425 );
            }
        }
    }

    [Test]
    public async Task External_log_filter_check_Async()
    {
        // Resets the default global filter.
        ActivityMonitor.DefaultFilter = LogFilter.Trace;
        await using( var g = new GrandOutput( new GrandOutputConfiguration() ) )
        {
            g.ExternalLogLevelFilter.Should().Be( LogLevelFilter.None );
            g.IsExternalLogEnabled( LogLevel.Debug ).Should().BeFalse();
            g.IsExternalLogEnabled( LogLevel.Trace ).Should().BeTrue();
            g.IsExternalLogEnabled( LogLevel.Info ).Should().BeTrue();
            ActivityMonitor.DefaultFilter = LogFilter.Release;
            g.IsExternalLogEnabled( LogLevel.Info ).Should().BeFalse();
            g.IsExternalLogEnabled( LogLevel.Warn ).Should().BeFalse();
            g.IsExternalLogEnabled( LogLevel.Error ).Should().BeTrue();
            g.ExternalLogLevelFilter = LogLevelFilter.Info;
            g.IsExternalLogEnabled( LogLevel.Trace ).Should().BeFalse();
            g.IsExternalLogEnabled( LogLevel.Info ).Should().BeTrue();
            g.IsExternalLogEnabled( LogLevel.Warn ).Should().BeTrue();
            g.IsExternalLogEnabled( LogLevel.Error ).Should().BeTrue();
        }
        ActivityMonitor.DefaultFilter = LogFilter.Trace;
    }

    static IActivityMonitor CreateMonitorAndRegisterGrandOutput( string topic, GrandOutput go )
    {
        var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration, topic: topic );
        go.EnsureGrandOutputClient( m );
        return m;
    }

    [Test]
    public async Task GrandOutput_MinimalFilter_works_Async()
    {
        await using GrandOutput go = new GrandOutput( new GrandOutputConfiguration() );
        var m = CreateMonitorAndRegisterGrandOutput( "Test.", go );
        m.ActualFilter.Should().Be( LogFilter.Undefined );
        go.MinimalFilter = LogFilter.Release;
        m.ActualFilter.Should().Be( LogFilter.Release );
    }

    [Test]
    public async Task GrandOutput_signals_its_disposing_via_a_CancellationToken_Async()
    {
        GrandOutput go = new GrandOutput( new GrandOutputConfiguration() );
        go.StoppedToken.IsCancellationRequested.Should().BeFalse();
        await go.DisposeAsync();
        go.StoppedToken.IsCancellationRequested.Should().BeTrue();
    }

    [TestCase( 1 )]
    public async Task disposing_GrandOutput_waits_for_termination_Async( int loop )
    {
        string logPath = TestHelper.PrepareLogFolder( "Termination" );
        var c = new GrandOutputConfiguration()
                        .AddHandler( new SlowSinkHandlerConfiguration() { Delay = 1 } )
                        .AddHandler( new Handlers.TextFileConfiguration() { Path = logPath } )
                        .AddHandler( new Handlers.BinaryFileConfiguration() { Path = logPath } );
        GrandOutputMemoryCollector inMemory;
        await using( var g = new GrandOutput( c ) )
        {
            inMemory = g.CreateMemoryCollector( 2000 * loop, ignoreCloseGroup: false );
            var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
            g.EnsureGrandOutputClient( m );
            DumpMonitor1083Entries( m, loop );
            inMemory.IsDisposed.Should().BeFalse();
        }
        inMemory.IsDisposed.Should().BeTrue();
        // Some entries can be the identitycard.
        inMemory.Count.Should().BeGreaterThanOrEqualTo( 1083 * loop );
        // All temporary files have been closed.
        var fileNames = Directory.EnumerateFiles( logPath ).ToList();
        fileNames.Should().NotContain( s => s.EndsWith( ".tmp" ) );
        // The {loop} "~~~~~FINAL TRACE~~~~~" appear in text logs.
        fileNames
            .Where( n => n.EndsWith( ".log" ) )
            .Select( n => File.ReadAllText( n ) )
            .Select( t => Regex.Matches( t, "~~~~~FINAL TRACE~~~~~" ).Count )
            .Sum()
            .Should().Be( loop );
    }

    // Caution: In the first run one entry is the IdentityCard!
    static void DumpMonitor1083Entries( IActivityMonitor monitor, int count )
    {
        const int nbLoop = 180;
        // Entry count per count = 2 + 1 + 180 * 6 = 1083
        // Entry count (for count parameter = 5): 5415
        //      this fits into the default per file count of 20000.
        for( int i = 0; i < count; i++ )
        {
            using( monitor.OpenTrace( $"Dump output loop {i}" ) )
            {
                for( int j = 0; j < nbLoop; j++ )
                {
                    // Debug is not sent.
                    monitor.Debug( $"Debug log! {j}" );
                    // These ar sent.
                    monitor.Trace( $"Trace log! {j}" );
                    monitor.Info( $"Info log! {j}" );
                    monitor.Warn( $"Warn log! {j}" );
                    monitor.Error( $"Error log! {j}" );
                    monitor.Fatal( $"Fatal log! {j}" );
                    monitor.Error( "Exception log! {j}", _exception2 );
                }
            }
            monitor.Info( "~~~~~FINAL TRACE~~~~~" );
        }
    }

}
