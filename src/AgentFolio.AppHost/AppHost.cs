var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.AgentFolio_Api>("api")
    .WithExternalHttpEndpoints();

builder.AddKubernetesEnvironment("k8s");

builder.Build().Run();
