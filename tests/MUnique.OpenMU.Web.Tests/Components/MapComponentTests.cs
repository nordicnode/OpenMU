// <copyright file="MapComponentTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Components;

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MUnique.OpenMU.Web.Map;
using MUnique.OpenMU.Web.Map.Map;
using MapComponent = MUnique.OpenMU.Web.Map.Components.Map;

/// <summary>
/// Tests for the <see cref="MapComponent"/> component of the live map.
/// </summary>
[TestFixture]
public class MapComponentTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private BunitContext _context = null!;

    /// <summary>
    /// Setups the test objects.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        this._context = new BunitContext();
        this._context.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>
    /// Tears down the test objects.
    /// </summary>
    [TearDown]
    public void Teardown()
    {
        this._context.Dispose();
    }

    /// <summary>
    /// Tests that a map controller whose creation completes after the component was disposed is
    /// disposed as well, instead of staying registered as an observer of the game map.
    /// </summary>
    /// <returns>The task of this test.</returns>
    [Test]
    public async Task ControllerCreatedAfterDisposalIsDisposedAsync()
    {
        var creation = new TaskCompletionSource<IMapController>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controllerDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mapRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new Mock<IMapController>();
        controller.Setup(c => c.DisposeAsync()).Returns(() =>
        {
            controllerDisposed.TrySetResult();
            return ValueTask.CompletedTask;
        });
        var factory = new Mock<IMapFactory>();
        factory.Setup(f => f.GetMapContainerIdentifier(It.IsAny<int>(), It.IsAny<Guid>())).Returns("map_0");
        factory.Setup(f => f.CreateMapAsync(It.IsAny<IObservableGameServer>(), It.IsAny<Guid>()))
            .Returns(() =>
            {
                mapRequested.TrySetResult();
                return new ValueTask<IMapController>(creation.Task);
            });
        this._context.Services.AddSingleton(factory.Object);

        this._context.Render<MapComponent>(parameters => parameters
            .Add(p => p.Server, new Mock<IObservableGameServer>().Object)
            .Add(p => p.MapId, Guid.NewGuid()));

        // The component creates the map lazily, when the live map asks for it after its first render.
        // Wait for that request itself instead of for a render which may carry it: the creation task
        // completes in the background, so there is no further render to retry an assertion on - which
        // made this test depend on the speed of the machine.
        Assert.That(await Task.WhenAny(mapRequested.Task, Task.Delay(Timeout)).ConfigureAwait(false), Is.SameAs(mapRequested.Task), "the component requests its map");
        factory.Verify(f => f.CreateMapAsync(It.IsAny<IObservableGameServer>(), It.IsAny<Guid>()), Times.Once);

        await this._context.DisposeComponentsAsync().ConfigureAwait(false);
        creation.SetResult(controller.Object);

        Assert.That(await Task.WhenAny(controllerDisposed.Task, Task.Delay(Timeout)).ConfigureAwait(false), Is.SameAs(controllerDisposed.Task));
    }
}
