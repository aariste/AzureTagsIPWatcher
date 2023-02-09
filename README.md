# Azure IP ranges and service tags monitor

This repo contains an Azure function that will query the Azure REST API to get the IP address ranges of a service and region.

The function stores the ranges in an Azure Table, which is later used to compare to newer runs if any difference exits.

You can find more information about how to use it in my blog:  [Dynamics 365 F&O and firewalls: monitor Azure IP ranges](https://ariste.info/en/2023/02/dynamics-365-firewall-monitor-azure-ip/)
