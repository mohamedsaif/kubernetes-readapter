using Json.Patch;
using k8s;
using k8s.Models;
using System.Text.Json;

namespace NodeDNSResolver
{
    public class Worker : BackgroundService
    {
        const string kubesystem = "kube-system";
        const string corednsConfigName = "coredns-custom";
        const string corednsDeploymentName = "coredns";
        const string restartAnnotation = "kubectl.kubernetes.io/restartedAt";
        const string configMapTab = "              ";

        private readonly ILogger<Worker> _logger;

        private bool isExecuted = false;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (isExecuted)
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                _logger.LogInformation("Starting kubernetes client at: {time}", DateTimeOffset.Now);

                await UpdateConfig();
            }
        }

        protected async Task UpdateConfig()
        {
            KubernetesClientConfiguration kubernetesClientConfiguration = null;
            if (Environment.GetEnvironmentVariable("ENV") != null)
            {
                kubernetesClientConfiguration = KubernetesClientConfiguration.InClusterConfig();
                _logger.LogInformation("using (In Cluster) configuation");
            }
            else
            {
                kubernetesClientConfiguration = KubernetesClientConfiguration.BuildDefaultConfig(); // for local debugging use kubernetes default config
                _logger.LogInformation("using (Default) configuation");
            }

            IKubernetes client = new Kubernetes(kubernetesClientConfiguration);

            var nodes = client.ListNode();

            var nodesIPs = new Dictionary<string, string>();

            if (nodes != null)
            {
                _logger.LogInformation($"Found ({nodes.Items.Count}) nodes");
                foreach (var node in nodes.Items)
                {
                    string ip = "";
                    string hostname = "";

                    foreach (var address in node.Status.Addresses)
                    {
                        if (address.Type == "Hostname")
                            hostname = address.Address;
                        else if (address.Type == "InternalIP")
                            ip = address.Address;
                    }

                    if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(hostname))
                    {
                        nodesIPs.Add(ip, hostname);
                        _logger.LogInformation($"Added host ({hostname}) with ({ip}) to config");
                    }
                }

                // construct the new updated configmap
                V1ObjectMeta configMeta = new V1ObjectMeta
                {
                    Name = corednsConfigName,
                    NamespaceProperty = kubesystem
                };
                V1ConfigMap corednsCM = new V1ConfigMap(metadata: configMeta);

                string hosts = "";

                foreach (var nodeIP in nodesIPs.Keys)
                {
                    if (hosts == "")
                    {
                        hosts = $"{nodeIP} {nodesIPs.GetValueOrDefault(nodeIP)}";
                    }
                    else
                    {
                        hosts += $"{Environment.NewLine}{configMapTab}{nodeIP} {nodesIPs.GetValueOrDefault(nodeIP)}";
                    }
                }

                _logger.LogInformation($"Final hosts: (({hosts}))");

                string templateConfigMapYaml = File.ReadAllText("hosts-cm.yaml");
                string updatedConfigMapYaml = templateConfigMapYaml.Replace("REPLACE", hosts);
                File.WriteAllText("updated-cm.yaml", updatedConfigMapYaml);

                var corednsCustomCM = await KubernetesYaml.LoadFromFileAsync<V1ConfigMap>("updated-cm.yaml");
                var currentCM = client.ReadNamespacedConfigMap(corednsConfigName, kubesystem);
                var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
                var oldCM = JsonSerializer.SerializeToDocument(currentCM, options);
                var newPatch = oldCM.CreatePatch(JsonSerializer.SerializeToDocument(corednsCustomCM, options));
                try
                {
                    var patch = new V1Patch(newPatch, V1Patch.PatchType.JsonPatch);
                    client.PatchNamespacedConfigMap(patch, corednsConfigName, kubesystem);
                    _logger.LogInformation("ConfigMap replaced");
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex.Message);
                    if (ex.Message.Contains("NotFound"))
                    {
                        client.CreateNamespacedConfigMap(corednsCustomCM, kubesystem);
                        _logger.LogInformation("ConfigMap created");
                    }
                    else
                    {
                        _logger.LogError("ConfigMap update failed", ex);
                        throw;
                    }
                }

                //coredns restart logic
                // adding annotation kubectl.kubernetes.io/restartedAt: '2006-01-02T15:04:05Z07:00'
                _logger.LogInformation("ConfigMap update operation completed successfully");

                var corednsDeployment = client.ReadNamespacedDeployment(corednsDeploymentName, kubesystem);
                var exists = corednsDeployment.Spec.Template.Metadata.Annotations.ContainsKey(restartAnnotation);
                if (exists)
                    corednsDeployment.Spec.Template.Metadata.Annotations.Remove(restartAnnotation);

                corednsDeployment.Spec.Template.Metadata.Annotations.Add(new KeyValuePair<string, string>("kubectl.kubernetes.io/restartedAt", DateTime.Now.ToString()));

                _logger.LogInformation("CoreDNS restart initiated successfully");

                isExecuted = true;
            }
        }
    }
}