using System.Threading;
using System.Threading.Tasks;
using XiyiDesktop.Widgets.Discovery;
using XiyiDesktop.Widgets.Models;

namespace XiyiDesktop.Widgets.Services;

internal sealed class WidgetCatalogService(WindowsWidgetDiscoveryService discovery)
{
    public Task<WidgetDiscoverySnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
        discovery.DiscoverAsync(cancellationToken);
}
