using DockerDiagram.Diagram;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Tests;

public sealed class ConnectorLifetimeTests
{
    [Fact]
    public void CollectionRemovalDetachesAndReAddAttachesEndpoints()
    {
        var source = new TrackingConnectableItem("source", 0, 0);
        var target = new TrackingConnectableItem("target", 200, 0);
        var connector = new ConnectorViewModel(source, target, PortDirection.Right, PortDirection.Left, null!);
        var collection = new ConnectorCollection();

        Assert.Equal(1, source.SubscriberCount);
        Assert.Equal(1, target.SubscriberCount);

        collection.Add(connector);
        Assert.Equal(1, source.SubscriberCount);
        Assert.Equal(1, target.SubscriberCount);

        collection.Remove(connector);
        Assert.Equal(0, source.SubscriberCount);
        Assert.Equal(0, target.SubscriberCount);

        collection.Add(connector);
        Assert.Equal(1, source.SubscriberCount);
        Assert.Equal(1, target.SubscriberCount);

        collection.Clear();
        Assert.Equal(0, source.SubscriberCount);
        Assert.Equal(0, target.SubscriberCount);
    }

    [Fact]
    public void SelfConnectionSubscribesOnlyOnce()
    {
        var endpoint = new TrackingConnectableItem("self", 0, 0);
        var connector = new ConnectorViewModel(endpoint, endpoint, PortDirection.Right, PortDirection.Left, null!);
        var collection = new ConnectorCollection { connector };

        Assert.Equal(1, endpoint.SubscriberCount);

        collection.Clear();

        Assert.Equal(0, endpoint.SubscriberCount);
    }

    private sealed class TrackingConnectableItem : IConnectableItem
    {
        private EventHandler? _positionChanged;

        public TrackingConnectableItem(string id, double x, double y)
        {
            Id = id;
            Name = id;
            X = x;
            Y = y;
        }

        public int SubscriberCount { get; private set; }
        public string Id { get; }
        public string Name { get; }
        public double X { get; }
        public double Y { get; }
        public double Width => 160;
        public double Height => 80;
        public double CenterX => X + (Width / 2);
        public double CenterY => Y + (Height / 2);
        public bool UsePointRouting => false;

        public event EventHandler? OnPositionChanged
        {
            add
            {
                _positionChanged += value;
                SubscriberCount++;
            }
            remove
            {
                _positionChanged -= value;
                SubscriberCount--;
            }
        }
    }
}
