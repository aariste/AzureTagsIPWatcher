# AzureTagsIPWatcher

This repo contains an Azure function that will query the Azure REST API to get the IP address ranges of a service and region.

The function stores the ranges in an Azure Table, which is later used to compare to newer runs if any difference exits.
