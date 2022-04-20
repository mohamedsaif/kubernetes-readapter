# kubernetes-readapter
C# based implementation of Kubernetes Client lib to support advanced customization of the cluster

## Node DNS Resolver

The first use-case I created this project for allowing pods to resolve node IP using its host name while using [Azure Kubernetes Service (AKS)]() with custom DNS servers configured on the virtual network.

### Challenge

If you tried to resolve via k8s CoreDNS a node host name while you have **Azure provided DNS** on the virtual network you wouldn't have issue:

```bash
# Get one node name
AKS_NODE=$(kubectl get nodes -o jsonpath='{ .items[0].metadata.name }')
echo $AKS_NODE

# Get the CoreDNS service IP
CORE_DNS_SERVICE_IP=$(kubectl get service -n kube-system kube-dns -o jsonpath='{ .spec.clusterIP }')
echo $CORE_DNS_SERVICE_IP

# Creating a debug pod
kubectl debug node/$AKS_NODE -it --image=mcr.microsoft.com/aks/fundamental/base-ubuntu:v0.0.11

#########################################################################
# The following commands will run in the interactive debugging session: #
#########################################################################
nslookup
server 10.0.0.10 # Replace if you have different CoreDNS IP

# Replace the node names with names from your cluster
aks-sys-34350944-vmss000002.internal.cloudapp.net
# Server:         10.0.0.10
# Address:        10.0.0.10#53

# Non-authoritative answer:
# Name:   aks-sys-34350944-vmss000002.internal.cloudapp.net
# Address: 10.171.0.4

aks-sys-34350944-vmss000002
# Server:         10.0.0.10
# Address:        10.0.0.10#53

# Non-authoritative answer:
# Name:   aks-sys-34350944-vmss000002.zxzyjsmlnwlejnwjqpc5domb2d.ax.internal.cloudapp.net
# Address: 10.171.0.4

```

Now you will have different story when you have custom DNS servers on the virtual network as the central DNS servers will not be able to resolve these private DNS records that is created implicitly by Azure DNS.

### Solution Approaches

Implement a custom CoreDNS configuration to guide the resolution query.

#### Option 1: Using conditional forwarder

If your scenario is to resolve the fully qualified name of the nodes (like NODE.internal.cloudapp.net), then you should be okay with a custom forwarder to Azure DNS IP **168.63.129.16** for **internal.cloudapp.net** zone

```yaml

apiVersion: v1
kind: ConfigMap
metadata:
  name: coredns-custom
  namespace: kube-system
data:
  log.override: | # you may select any name here, but it must end with the .override file extension
        log
  azuredns.server: |
    internal.cloudapp.net {
        log
        errors
        forward . 168.63.129.16
    }

```

Key point here that you need to hit the Azure DNS IP from the same network as the AKS cluster (hitting the Azure DNS from outside the network will not result in successful resolution as these DNS records only exists within the current network)

#### Option 2: Using Hosts Block

In the scenario I'm trying to fix, I needed to be able to resolve the root host name of the node:

```bash

nslookup aks-sys-34350944-vmss000002

```

It is tricky to conditionally forward host names using built-in CoreDNS plugins like Regex rewrite for example.

So I decided to use **hosts** plugin.

Adding a hosts block to the CoreDNS custom configuation is easy enough if it is static:

```yaml

apiVersion: v1
kind: ConfigMap
metadata:
  name: coredns-custom
  namespace: kube-system
data:
    coredns-hosts.override: |
          hosts { 
              10.171.0.4 aks-sys-34350944-vmss000002
              10.171.0.35 aks-sys-34350944-vmss000003
              fallthrough
          }

```

Now the real challenge is the dynamic nature of node names and IPs that can change during scale event or upgrade for examples.

Here where I decided to built simple custom DaemonSet that query the node names and IPs, update the CoreDNS custom ConfigMap with hosts block, and finally gracefully restart the CoreDNS deployment to fitch updated configuration.

This solution is not perfect but get the job done.

## Node DNS Resolver Client Implementation

Under [src/NodeDNSResolver](src/NodeDNSResolver) I have simple .NET 6 background service that leverages the official Kubernetes Client library to implement the logic for updating the CoreDNS custom configuration with hosts block.

Below are the steps to build, push and deploy this client

### Build and push the client image

We need to have our client container image pushed to Azure Container Registry (ACR).

As this is very standard process, you can follow any process that get the job done.

Here I'm using ACR tasks to build and push the image to AKS connected registry

```bash

# make sure you are running these commands in the context of the source code folder
cd src/NodeDNSResolver

# Azure Container Registry name
ACR_NAME=REPLACE

# Make sure that the bash command line folder context set to the root where dockerfile exists

# OPTION 1: Using ACR Tasks
# With dynamic version
az acr build -t k8s-clients/nodednsresolver:{{.Run.ID}} -t k8s-clients/nodednsresolver:latest -r $ACR_NAME .

```

Once the image is pushed successfully take a note of the custom tag generated from ```{{.Run.ID}}``` if you want to deploy the image using that tag.

>>NOTE: It is highly recommended to use custom tags other than ```latest``` in production system to know exactly the version of the image running and also to leverage local image cache for faster deployments.

### Deploy to Kubernetes

Now it is time to deploy the DaemonSet to AKS.

Head to the folder named **k8s** under the project folder and update the image url to point to the pushed image.

Also take a note that I'm creating a new service account with a cluster role binding to **cluster-admin** to allow the pods to query the nodes, apply the updated ConfigMap and restart the CoreDNS deployment.

When you are ready, execute the following command to deploy all needed configuration:

```bash

kubectl apply -f ./k8s

```

### Validating

Now with the deployment up and running in your cluster, you can view the different changes using the following script:

```bash

# Get the node-resolver-ds pods
kubectl get pods --selector=k8s-app=node-resolver-ds -n kube-system

# Get one node name
AKS_NODE=$(kubectl get nodes -o jsonpath='{ .items[0].metadata.name }')
echo $AKS_NODE

# Get the CoreDNS service IP
CORE_DNS_SERVICE_IP=$(kubectl get service -n kube-system kube-dns -o jsonpath='{ .spec.clusterIP }')
echo $CORE_DNS_SERVICE_IP

# Creating a debug pod
kubectl debug node/$AKS_NODE -it --image=mcr.microsoft.com/aks/fundamental/base-ubuntu:v0.0.11

#########################################################################
# The following commands will run in the interactive debugging session: #
#########################################################################
nslookup
server 10.0.0.10 # Replace if you have different CoreDNS IP

# Replace the node names with names from your cluster
aks-sys-34350944-vmss000002
# Server:         10.0.0.10
# Address:        10.0.0.10#53

# Non-authoritative answer:
# Name:   aks-sys-34350944-vmss000002.
# Address: 10.171.0.4

exit

```

### Diagnosis

If you faced issues, these commands might be helpful in knowing why:

```bash

# These are the logs of all running pods
kubectl logs --selector=k8s-app=node-resolver-ds -n kube-system

# getting logs for the first pod only
POD_NAME=$(kubectl get pod --selector=k8s-app=node-resolver-ds -n kube-system -o jsonpath='{ .items[0].metadata.name }')
echo $POD_NAME
kubectl logs -n kube-system $POD_NAME

# Restarting the DaemonSet
kubectl rollout restart daemonset -n kube-system node-resolver-ds

```