using System.Net;
using PcDiag.Web.Hosting;

namespace PcDiag.Web.Tests;

public class ContainerSupportTests
{
    [Fact]
    public void Listens_on_loopback_outside_containers()
    {
        ListenAddress.Resolve(_ => null).ShouldBe(IPAddress.Loopback);
        ListenAddress.Resolve(_ => "false").ShouldBe(IPAddress.Loopback);
    }

    [Fact]
    public void Listens_on_all_container_interfaces_inside_a_container()
    {
        ListenAddress.Resolve(name => name == ListenAddress.ContainerVariable ? "true" : null).ShouldBe(IPAddress.Any);
    }

    [Theory]
    [InlineData("http://localhost:5180", true)]
    [InlineData("http://127.0.0.1:5180/", true)]
    [InlineData("http://192.168.1.20:5180", false)]
    [InlineData("ftp://localhost:5180", false)]
    [InlineData("localhost:5180", false)]
    public void Public_base_url_must_be_loopback_http(string url, bool valid)
    {
        new WebOptionsValidator().Validate(null, new WebOptions { PublicBaseUrl = url }).Succeeded.ShouldBe(valid);
    }
}
