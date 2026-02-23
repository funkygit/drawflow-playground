class WorkflowExecutor {
    constructor(editor, connection, executionId, workflowId, nodeMetaList, onChainExecution = null) {
        this.editor = editor;
        this.connection = connection;
        this.executionId = executionId;
        this.workflowId = workflowId;
        this.nodeMetaList = nodeMetaList;
        this.onChainExecution = onChainExecution;
        this.nodeResults = {};
    }

    async start() {
        const data = this.editor.export();
        // Safety check for Home module
        if (!data.drawflow.Home || !data.drawflow.Home.data) {
             console.warn("Workflow data empty or invalid.");
             return;
        }

        const nodes = data.drawflow.Home.data;

        // Queue Start Nodes
        for (const nodeId in nodes) {
            const node = nodes[nodeId];
			
            // Check if there are any active incoming connections
            let hasIncomingConnections = false;
            for (const inputKey in node.inputs) {
                if (node.inputs[inputKey].connections && node.inputs[inputKey].connections.length > 0) {
                    hasIncomingConnections = true;
                    break;
                }
            }
			
            // Convention: Start nodes have no incoming connections OR are explicitly named 'start'
            if (!hasIncomingConnections || node.name.toLowerCase() === 'start') {
                this.log(`Queueing Start Node ${nodeId}...`);
                const queueItem = this.buildQueueItem(nodeId, nodes);
                await this.connection.invoke("QueueNode", queueItem);
            }
        }
    }

    /**
     * Builds an ExecutionQueueItem from the node data and connections.
     * @param {string} nodeId - The Drawflow node ID.
     * @param {Object} nodes - All nodes from the exported Drawflow data.
     * @param {string} [completedNodeId] - The parent node that triggered this queue (for input resolution).
     * @returns {Object} An ExecutionQueueItem-compatible object for SignalR.
     */
    buildQueueItem(nodeId, nodes, completedNodeId = null) {
        const node = nodes[nodeId];
        const nodeData = node.data || {};

        const nodeKey = nodeData.nodeKey || null;
        const nodeType = nodeData.nodeType || node.name || 'action';

        // Build parameters by scraping the saved data.parameters
        const parameters = this._buildParameters(nodeData);

        // Resolve NodeInput from connections
        const input = this._resolveInput(nodeId, nodes, completedNodeId);

        return {
            executionId: this.executionId,
            nodeId: nodeId,
            nodeKey: nodeKey,
            nodeType: nodeType,
            parameters: parameters,
            input: input
        };
    }

    /**
     * Builds a List<NodeParameterMeta> from the node's saved parameters and variant selection.
     * Scrapes the stored data.parameters (flat { name: value } map) and uses nodeMetaList
     * to supplement with dataType. Includes the variant source as a parameter entry.
     */
    _buildParameters(nodeData) {
        const params = [];
        const nodeKey = nodeData.nodeKey;
        const meta = nodeKey ? this.nodeMetaList.find(m => m.nodeKey === nodeKey) : null;

        // Include variant source as a parameter if applicable
        if (meta && meta.variantSource && nodeData.selectedVariant) {
            params.push({
                name: meta.variantSource,
                value: nodeData.selectedVariant,
                dataType: 'System.String',
                source: 'USER_INPUT'
            });
        }

        // Convert saved parameters { name: value } into NodeParameterMeta list
        if (nodeData.parameters) {
            // Find the active variant's parameter metadata for dataType lookup
            let variantMeta = null;
            if (meta && meta.variants) {
                variantMeta = meta.variants.find(v => v.value === nodeData.selectedVariant);
                if (!variantMeta) variantMeta = meta.variants[0]; // fallback to first
            }

            Object.keys(nodeData.parameters).forEach(paramName => {
                // Skip if this is the variant source (already added above)
                if (meta && meta.variantSource && paramName === meta.variantSource) return;

                const paramValue = nodeData.parameters[paramName];

                // Look up dataType from variant metadata, default to System.String
                let dataType = 'System.String';
                let source = 'USER_INPUT';
                if (variantMeta && variantMeta.parameters) {
                    const paramMeta = variantMeta.parameters.find(p => p.name === paramName);
                    if (paramMeta) {
                        dataType = paramMeta.dataType || 'System.String';
                        source = paramMeta.source || 'USER_INPUT';
                    }
                }

                params.push({
                    name: paramName,
                    value: paramValue,
                    dataType: dataType,
                    source: source
                });
            });
        }

        return params;
    }

    /**
     * Resolves NodeInput from Drawflow connections.
     * Finds the connection from completedNodeId to this node, or the first input connection.
     */
    _resolveInput(nodeId, nodes, completedNodeId) {
        const node = nodes[nodeId];
        if (!node || !node.inputs) return null;

        for (const inputKey in node.inputs) {
            const connections = node.inputs[inputKey].connections;
            if (connections && connections.length > 0) {
                for (const conn of connections) {
                    // If a specific parent triggered this, prefer that connection
                    if (completedNodeId && conn.node !== completedNodeId) continue;

                    const sourceNodeId = conn.node;
                    const sourceNode = nodes[sourceNodeId];

                    // Resolve the output name from the connection
                    // conn.output is the Drawflow output key (e.g., "output_1")
                    // Map to the actual output name from the node's meta
                    let sourceOutputName = conn.output;

                    if (sourceNode && sourceNode.data && sourceNode.data.nodeKey) {
                        const sourceMeta = this.nodeMetaList.find(m => m.nodeKey === sourceNode.data.nodeKey);
                        if (sourceMeta) {
                            const sourceVariant = sourceMeta.variants?.find(v => v.value === sourceNode.data.selectedVariant)
                                || sourceMeta.variants?.[0];
                            if (sourceVariant && sourceVariant.outputs) {
                                // Map output_1 -> index 0, output_2 -> index 1, etc.
                                const outputIndex = parseInt(conn.output.replace('output_', '')) - 1;
                                if (sourceVariant.outputs[outputIndex]) {
                                    sourceOutputName = sourceVariant.outputs[outputIndex].name;
                                }
                            }
                        }
                    }

                    return {
                        sourceNodeId: sourceNodeId,
                        sourceOutputName: sourceOutputName
                    };
                }
            }
        }

        return null;
    }

    async handleNodeCompletion(nodeId, triggeredOutput = null) {
        this.nodeResults[nodeId] = "Completed";
        await this._checkAndQueueChildren(nodeId, triggeredOutput);
    }

    async _checkAndQueueChildren(completedNodeId, triggeredOutput = null) {
        const data = this.editor.export();
        if (!data.drawflow.Home || !data.drawflow.Home.data) return;
        
        const nodes = data.drawflow.Home.data;
        const completedNode = nodes[completedNodeId];
        
        if (!completedNode) return;

        // Find children
        const outputs = completedNode.outputs;
        const childrenIds = new Set();
        
        for (const key in outputs) {
            // If triggeredOutput is set, only queue children from that specific output
            if (triggeredOutput && key !== triggeredOutput) continue;

            const connections = outputs[key].connections;
            connections.forEach(conn => childrenIds.add(conn.node));
        }

        for (const childId of childrenIds) {
            if (this._canQueue(childId, nodes)) {
                 this.log(`Queueing Node ${childId}...`);
                 const queueItem = this.buildQueueItem(childId, nodes, completedNodeId);
                 await this.connection.invoke("QueueNode", queueItem);
            }
        }
        
        // Check if End node
        if (completedNode.name.toLowerCase() === 'end') {
            this.log("Workflow Completed (End Node Reached)");
        }
    }

    _canQueue(childId, allNodes) {
        const childNode = allNodes[childId];
        if (!childNode) return false;

        // Check Inputs
        const inputs = childNode.inputs;
        let hasInputs = false;
        let allParentsReady = true;
        let anyParentReady = false;

        for (const key in inputs) {
            const connections = inputs[key].connections;
            connections.forEach(conn => {
                hasInputs = true;
                const parentId = conn.node;
                // Check if parent completed in THIS execution instance
                if (this.nodeResults[parentId] === "Completed") {
                    anyParentReady = true;
                } else {
                    allParentsReady = false;
                }
            });
        }

        if (!hasInputs) return true; 

        const nodeName = childNode.name.toLowerCase();
        const isLoopOrMerge = nodeName.includes("loop") || nodeName.includes("merge");

        if (isLoopOrMerge) {
            return anyParentReady; 
        } else {
            return allParentsReady;
        }
    }

    log(msg) {
        // Optional: Custom logger or console
        console.log(`[Exec ${this.executionId}] ${msg}`);
    }
}