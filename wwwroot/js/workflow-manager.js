class WorkflowExecutor {
    constructor(editor, connection, executionId, workflowId, onChainExecution = null) {
        this.editor = editor;
        this.connection = connection;
        this.executionId = executionId;
        this.workflowId = workflowId;
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
            const inputCount = Object.keys(node.inputs).length;
            // Convention: Start nodes have no inputs OR are explicitly named 'start'
            if (inputCount === 0 || node.name.toLowerCase() === 'start') {
                this.log(`Queueing Start Node ${nodeId}...`);
                await this.connection.invoke("QueueNode", this.executionId, nodeId, "start");
            }
        }
    }

    async handleNodeCompletion(nodeId) {
        this.nodeResults[nodeId] = "Completed";
        
        // Loop Logic: Removed. Loop nodes now have outputs and connect back to other nodes. This allows for cycles.

        await this._checkAndQueueChildren(nodeId);
    }

    async _checkAndQueueChildren(completedNodeId) {
        const data = this.editor.export();
        if (!data.drawflow.Home || !data.drawflow.Home.data) return;
        
        const nodes = data.drawflow.Home.data;
        const completedNode = nodes[completedNodeId];
        
        if (!completedNode) return;

        // Find children
        const outputs = completedNode.outputs;
        const childrenIds = new Set();
        
        for (const key in outputs) {
            const connections = outputs[key].connections;
            connections.forEach(conn => childrenIds.add(conn.node));
        }

        for (const childId of childrenIds) {
            if (this._canQueue(childId, nodes)) {
                 const childNode = nodes[childId];
                 const type = childNode.name.toLowerCase().includes("delay") ? "delay" : "action";
                 this.log(`Queueing Node ${childId} (${type})...`);
                 await this.connection.invoke("QueueNode", this.executionId, childId, type);
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
 