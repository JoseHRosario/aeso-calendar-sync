using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
namespace Aeso.CalendarSync.Tests;

public sealed class FakeSsm(GetParametersResponse response) : AmazonSimpleSystemsManagementClient(new AnonymousAWSCredentials(), Amazon.RegionEndpoint.EUWest1)
{
    public int Calls { get; private set; }
    public GetParametersRequest? Request { get; private set; }
    public Exception? Failure { get; set; }
    public override Task<GetParametersResponse> GetParametersAsync(GetParametersRequest request, CancellationToken ct = default)
    {
        Calls++; Request = request;
        return Failure is null ? Task.FromResult(response) : Task.FromException<GetParametersResponse>(Failure);
    }
}
