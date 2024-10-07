using CK.AspNet.Tester;
using CK.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using System.IO;
using System.Linq;

namespace CK.Monitoring.Hosting.Tests;

[TestFixture]
public class BuilderMonitorTests
{
    [SetUp]
    public void InitializePath()
    {
        TestHelper.InitalizePaths();
        TestHelper.WaitForNoMoreAliveInputLogEntry();
    }

    [Test]
    public void BuilderMonitor_is_always_available()
    {
        string folder = TestHelper.PrepareLogFolder( "FromBuilderMonitor" );
        // Disposes current GrandOutput.Default if any.
        GrandOutput.Default?.Dispose();

        // The GetBuilderMonitor() is available.
        var builder = Host.CreateEmptyApplicationBuilder( new HostApplicationBuilderSettings { DisableDefaults = true } );
        builder.GetBuilderMonitor().Info( "This will eventually be logged!" );

        // And we configure the text log output...
        // This works because the BuilderMonitor retains and replays the logs received before the
        // GrandOutput and its handlers are made available.
        var config = new DynamicConfigurationSource();
        config["CK-Monitoring:GrandOutput:Handlers:TextFile:Path"] = "FromBuilderMonitor";
        builder.Configuration.Sources.Add( config );
        builder.UseCKMonitoring();

        builder.GetBuilderMonitor().Info( "After configure." );

        var app = builder.Build();
        GrandOutput.Default!.Dispose();

        var text = TestHelper.FileReadAllText( Directory.EnumerateFiles( folder ).Single() );
        text.Should().Contain( "This will eventually be logged!" )
                 .And.Contain( "After configure." );

        // Just make sure that the replay doesn't leak.
        ActivityMonitorExternalLogData.AliveCount.Should().Be( 0 );
        InputLogEntry.AliveCount.Should().Be( 0 );
    }
}
