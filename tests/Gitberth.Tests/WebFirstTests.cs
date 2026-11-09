using Gitberth.Network;

namespace Gitberth.Tests;

/// <summary>
/// An app's row opens its web address: RabbitMQ publishes 4369, 5671, 5672, 15671, 15672, 15691, 15692 and 25672, and its
/// row must open the management console on 15672, not Erlang's port mapper on 4369.
/// </summary>
public sealed class WebFirstTests
{
    private static AddressChip Chip(int port, string protocol = "tcp") =>
        new($"{port}:{port}", "", AddressBook.IsWeb(port, protocol, database: false, appWebPort: false) ? AddressChipKind.Open : AddressChipKind.Copy,
            $"http://10.1.1.80:{port}", port, port, protocol);

    private static readonly int[] Rabbit = [4369, 5671, 5672, 15671, 15672, 15691, 15692, 25672];

    [Fact]
    public void Erlangs_and_the_brokers_own_ports_dont_open_in_a_browser()
    {
        foreach (var port in new[] { 4369, 5671, 5672, 25672, 61613 })
            Assert.False(AddressBook.IsWeb(port, "tcp", database: false, appWebPort: false), $"{port} isn't HTTP");
        Assert.True(AddressBook.IsWeb(15672, "tcp", database: false, appWebPort: false));
    }

    [Fact]
    public void The_apps_web_port_leads_its_chips()
    {
        var chips = AddressBook.WebFirst(Rabbit.Select(p => Chip(p)), webPort: 15672);

        Assert.Equal(15672, chips[0].ContainerPort);
        Assert.Equal(Rabbit.Where(p => p != 15672), chips.Skip(1).Select(c => c.ContainerPort));
    }

    [Fact]
    public void Without_a_known_web_port_a_brokers_console_leads()
    {
        var chips = AddressBook.WebFirst(Rabbit.Select(p => Chip(p)), webPort: null);

        Assert.Equal(15672, chips[0].ContainerPort);
        Assert.Equal(AddressChipKind.Open, chips[0].Kind);
    }

    [Fact]
    public void Chips_of_an_ordinary_app_keep_their_order()
    {
        var chips = AddressBook.WebFirst([Chip(3000), Chip(9229), Chip(53, "udp")], webPort: null);

        Assert.Equal([3000, 9229, 53], chips.Select(c => c.ContainerPort));
    }
}
