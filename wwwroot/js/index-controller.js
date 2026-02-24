var id = document.getElementById("drawflow");
const editor = new Drawflow(id);
editor.reroute = true;
editor.start();

// SignalR Setup
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/workflowHub")
    .build();

let currentWorkflowId = null;
let currentExecutor = null;
let nodeMetaList = [];

async function fetchNodeMeta() {
    try {
        const res = await fetch('/Home/GetNodeMeta');
        nodeMetaList = await res.json();
        renderAvailableNodes();
    } catch (err) {
        log("Error fetching node meta: " + err);
    }
}

function renderAvailableNodes() {
    const container = document.getElementById('available-nodes');
    container.innerHTML = '';

    // Group by NodeType and sort by NodeTypeOrder
    const grouped = nodeMetaList.reduce((acc, meta) => {
        const key = meta.nodeType || "Other";
        if (!acc[key]) acc[key] = { order: meta.nodeTypeOrder, list: [] };
        acc[key].list.push(meta);
        return acc;
    }, {});

    const sortedCategories = Object.keys(grouped).sort((a, b) => grouped[a].order - grouped[b].order);

    sortedCategories.forEach(cat => {
        const header = document.createElement('h6');
        header.className = 'mt-2 text-primary';
        header.innerText = cat;
        container.appendChild(header);

        grouped[cat].list.forEach(meta => {
            const item = document.createElement('div');
            item.className = 'list-group-item list-group-item-action py-1 px-2 small drag-drawflow';
            item.draggable = true;
            item.innerText = meta.displayName;
            item.dataset.node = 'module';
            item.dataset.nodeKey = meta.nodeKey;
            item.ondragstart = drag;
            container.appendChild(item);
        });
    });
}

fetchNodeMeta();

connection.on("NodeStatusChanged", (executionId, nodeId, status, triggeredOutput) => {
    // Filter for current execution
    if (currentExecutor && currentExecutor.executionId === executionId) {
        log(`Node ${nodeId}: ${status}${triggeredOutput ? ' [' + triggeredOutput + ']' : ''}`);
        updateNodeVisuals(nodeId, status);

        if (status === "Completed") {
            currentExecutor.handleNodeCompletion(nodeId, triggeredOutput);
        }
    }
});

connection.on("ExecutionPaused", (id) => log(`Execution ${id} PAUSED`));
connection.on("ExecutionResumed", (id) => log(`Execution ${id} RESUMED`));
connection.on("ExecutionAborted", (id) => log(`Execution ${id} ABORTED`));

connection.start().then(() => {
    log("Connected to SignalR");
}).catch(err => log("SignalR Error: " + err));

function updateNodeVisuals(nodeId, status) {
    const nodeEl = document.getElementById(`node-${nodeId}`);
    if (nodeEl) {
        let badge = nodeEl.querySelector('.node-status');
        if (!badge) {
            badge = document.createElement('div');
            badge.className = 'node-status';
            nodeEl.appendChild(badge);
        }
        badge.className = `node-status status-${status.toLowerCase()}`;
    }
}

// Drag & Drop
function drag(ev) {
    ev.dataTransfer.setData("node", ev.target.getAttribute('data-node'));
    if (ev.target.getAttribute('data-node-key')) {
        ev.dataTransfer.setData("nodeKey", ev.target.getAttribute('data-node-key'));
    }
}

id.addEventListener('drop', (ev) => {
    ev.preventDefault();
    const nodeType = ev.dataTransfer.getData("node");
    const nodeKey = ev.dataTransfer.getData("nodeKey");
    addNodeToDrawflow(nodeType, ev.clientX, ev.clientY, nodeKey);
});

id.addEventListener('dragover', (ev) => {
    ev.preventDefault();
});

// ============================================================
// Node Content HTML Builder
// ============================================================

/**
 * Builds the full HTML content for a Drawflow node.
 * Structure: parent-div.node-config-container > title-div > params-table
 * @param {Object} meta - Node metadata from nodeMetaList
 * @param {Object} data - Node data (nodeKey, selectedVariant, parameters, etc.)
 * @param {string|number} nodeId - The Drawflow node ID (may not be known yet at creation time)
 * @returns {string} HTML string for the node content
 */
function buildNodeContentHtml(meta, data, nodeId) {
    const hasVariants = meta.variants && meta.variants.length > 1;
    const selectedVariant = data.selectedVariant || (meta.variants && meta.variants.length > 0 ? meta.variants[0].value : null);

    let html = `<div class="node-config-container" data-node-key="${meta.nodeKey}">`;

    // Title row
    html += `<div class="node-title">`;
    html += `<span>${meta.displayName}</span>`;
    html += `<span class="node-id-badge">#<span class="node-id-value">${nodeId || '?'}</span></span>`;
    html += `</div>`;

    // Variant selector (if multiple variants)
    if (hasVariants) {
        html += `<div class="node-variant-row">`;
        html += `<label>${meta.variantSource || 'Variant'}</label>`;
        html += `<select class="node-variant-select">`;
        meta.variants.forEach(v => {
            const selected = v.value === selectedVariant ? ' selected' : '';
            html += `<option value="${v.value}"${selected}>${v.label}</option>`;
        });
        html += `</select>`;
        html += `</div>`;
    }

    // Parameters container (will be populated by DynamicFormRenderer after node is created)
    html += `<div class="node-params-container"></div>`;

    // Previous connected nodes display removed as it is handled via NODE_OUTPUT dropdowns

    // Update data button
    html += `<button class="node-update-btn" type="button">Update</button>`;

    html += `</div>`;
    return html;
}

/**
 * Renders the parameter table inside a node's content using DynamicFormRenderer.
 * Also populates existing values from data.parameters.
 */
function renderNodeParams(nodeId, data, meta) {
    const nodeEl = document.getElementById(`node-${nodeId}`);
    if (!nodeEl) return;

    const paramsContainer = nodeEl.querySelector('.node-params-container');
    if (!paramsContainer) return;

    const renderer = new DynamicFormRenderer(meta, (requiredType) => {
        return findConnectedPreviousNodes(nodeId, requiredType);
    });

    renderer.renderAllVariantsTable(paramsContainer, data.selectedVariant, (requiredType) => {
        return findConnectedPreviousNodes(nodeId, requiredType);
    });

    // Populate existing values if any
    if (data.parameters) {
        Object.keys(data.parameters).forEach(key => {
            const el = paramsContainer.querySelector(`[name="${key}"]`);
            if (el) el.value = data.parameters[key];
        });
    }
}



/**
 * Finds previous nodes connected to currentNodeId that output the requiredType.
 * Only considers nodes with actual Drawflow connections into the current node.
 */
function findConnectedPreviousNodes(currentNodeId, requiredType) {
    const data = editor.export();
    if (!data.drawflow.Home || !data.drawflow.Home.data) return [];

    const nodes = data.drawflow.Home.data;
    const currentNode = nodes[currentNodeId];
    if (!currentNode || !currentNode.inputs) return [];

    const compatible = [];
    const seenIds = new Set();

    // Get all connected input node IDs
    for (const inputKey in currentNode.inputs) {
        const connections = currentNode.inputs[inputKey].connections;
        if (connections) {
            connections.forEach(conn => seenIds.add(conn.node));
        }
    }

    // Check each connected node for matching output types
    seenIds.forEach(sourceId => {
        const sourceNode = nodes[sourceId];
        if (!sourceNode || !sourceNode.data || !sourceNode.data.nodeKey) return;

        const meta = nodeMetaList.find(m => m.nodeKey === sourceNode.data.nodeKey);
        const variant = meta?.variants.find(v => v.value === sourceNode.data.selectedVariant);

        if (variant && variant.outputs) {
            variant.outputs.forEach(output => {
                if (output.dataType === requiredType || requiredType === 'System.Object') {
                    compatible.push({
                        nodeId: sourceId,
                        outputName: output.name,
                        label: `${sourceNode.data.displayName} (${output.name})`
                    });
                }
            });
        }
    });

    return compatible;
}

// ============================================================
// Add Node to Drawflow
// ============================================================

function addNodeToDrawflow(type, posx, posy, nodeKey = null) {
    if (editor.editor_mode === 'fixed') return false;
    posx = posx * (editor.precanvas.clientWidth / (editor.precanvas.clientWidth * editor.zoom)) - (editor.precanvas.getBoundingClientRect().x * (editor.precanvas.clientWidth / (editor.precanvas.clientWidth * editor.zoom)));
    posy = posy * (editor.precanvas.clientHeight / (editor.precanvas.clientHeight * editor.zoom)) - (editor.precanvas.getBoundingClientRect().y * (editor.precanvas.clientHeight / (editor.precanvas.clientHeight * editor.zoom)));

    let inputs = 1;
    let outputs = 1;
    let html = `<div>${type.toUpperCase()}</div>`;
    let data = { name: type };

    if (nodeKey) {
        const meta = nodeMetaList.find(m => m.nodeKey === nodeKey);
        if (meta) {
            data = {
                nodeKey: nodeKey,
                displayName: meta.displayName,
                nodeType: meta.nodeType,
                selectedVariant: meta.variants && meta.variants.length > 0 ? meta.variants[0].value : null,
                parameters: {} // To be filled by config
            };

            // Build the rich in-node HTML (nodeId not yet known, will be patched)
            html = buildNodeContentHtml(meta, data, '?');

            // Derive input/output counts from variant metadata
            const defaultVariant = meta.variants && meta.variants.length > 0 ? meta.variants[0] : null;

            // Control nodes (Conditional, Iterator) use outputs.length for multiple execution ports
            // Processing nodes always get 1 output port (data outputs are fields within a single result)
            if (meta.nodeTypeKey === 'control') {
                const outputCount = defaultVariant && defaultVariant.outputs ? defaultVariant.outputs.length : 0;
                outputs = outputCount;
            }
            if (meta.nodeTypeKey === 'input') { inputs = 0; }
            if (meta.nodeTypeKey === 'output' || meta.nodeKey === 'end') { outputs = 0; }
        }
    } else {
        if (type === 'start') { inputs = 0; outputs = 1; }
        if (type === 'end') { inputs = 1; outputs = 0; }
        if (type === 'loop') { inputs = 1; outputs = 1; }
        if (type === 'conditional') { inputs = 1; outputs = 2; }
        if (type === 'iterator') { inputs = 1; outputs = 2; }
    }

    const newNodeId = editor.addNode(type, inputs, outputs, posx, posy, type, data, html);

    // After creation, patch the node ID into the content and render params
    if (nodeKey) {
        const meta = nodeMetaList.find(m => m.nodeKey === nodeKey);
        if (meta) {
            // Patch the node ID badge
            const nodeEl = document.getElementById(`node-${newNodeId}`);
            if (nodeEl) {
                const idBadge = nodeEl.querySelector('.node-id-value');
                if (idBadge) idBadge.textContent = newNodeId;
            }

            // Render parameters table
            renderNodeParams(newNodeId, data, meta);

            // Attach event listeners
            attachNodeEventListeners(newNodeId, data, meta);
        }
    }
}

/**
 * Attaches interactive event listeners to the in-node controls.
 */
function attachNodeEventListeners(nodeId, data, meta) {
    const nodeEl = document.getElementById(`node-${nodeId}`);
    if (!nodeEl) return;

    // Variant selector change
    const variantSelect = nodeEl.querySelector('.node-variant-select');
    if (variantSelect) {
        variantSelect.addEventListener('change', (e) => {
            e.stopPropagation();
            data.selectedVariant = e.target.value;

            // Toggle parameter row visibility
            const paramsContainer = nodeEl.querySelector('.node-params-container');
            if (paramsContainer) {
                const renderer = new DynamicFormRenderer(meta, () => []);
                renderer.toggleVariantVisibility(paramsContainer, data.selectedVariant);
            }

            log(`Node ${nodeId}: Variant changed to ${data.selectedVariant}`);
        });
    }

    // Update button
    const updateBtn = nodeEl.querySelector('.node-update-btn');
    if (updateBtn) {
        updateBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            updateNodeDataFromContent(nodeId);
        });
    }

    // Prevent click propagation on inputs/selects so Drawflow doesn't interfere
    nodeEl.querySelectorAll('input, select, button').forEach(el => {
        el.addEventListener('mousedown', (e) => e.stopPropagation());
        el.addEventListener('click', (e) => e.stopPropagation());
    });
}

// ============================================================
// Update Node Data from In-Node Content
// ============================================================

function updateNodeDataFromContent(nodeId) {
    const node = editor.getNodeFromId(nodeId);
    if (!node || !node.data) return;

    const nodeEl = document.getElementById(`node-${nodeId}`);
    if (!nodeEl) return;

    const paramsContainer = nodeEl.querySelector('.node-params-container');
    if (!paramsContainer) return;

    const inputs = paramsContainer.querySelectorAll('input, select');
    node.data.parameters = {};
    inputs.forEach(input => {
        if (input.name) {
            node.data.parameters[input.name] = input.value;
        }
    });

    log(`Updated Node ${nodeId} data`);
}

// ============================================================
// API Calls
// ============================================================

async function saveWorkflow() {
    const data = editor.export();
    const workflow = {
        Id: currentWorkflowId || undefined,
        Name: "My Workflow " + new Date().toISOString(),
        JsonData: JSON.stringify(data)
    };

    const res = await fetch('/Home/SaveWorkflow', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(workflow)
    });
    const result = await res.json();
    currentWorkflowId = result.id;
    log(`Workflow Saved: ${currentWorkflowId}`);
}

async function runWorkflow() {
    if (!currentWorkflowId) {
        await saveWorkflow();
    }

    // Reset Visuals
    document.querySelectorAll('.node-status').forEach(el => el.remove());

    const executionId = await connection.invoke("StartWorkflow", currentWorkflowId, null);
    log(`Started Execution: ${executionId}`);

    await connection.invoke("JoinWorkflowGroup", executionId);

    // Init Executor
    currentExecutor = new WorkflowExecutor(editor, connection, executionId, currentWorkflowId, nodeMetaList, async (newExecId) => {
        // Handle Loop: Switch context to new Execution ID
        currentExecutor = new WorkflowExecutor(editor, connection, newExecId, currentWorkflowId, nodeMetaList, currentExecutor.onChainExecution);
        // Join new group
        connection.invoke("JoinWorkflowGroup", newExecId);
        // Clear visuals for new run
        document.querySelectorAll('.node-status').forEach(el => el.remove());
        log(`Switched to Loop Execution: ${newExecId}`);
        await currentExecutor.start();
    });
    await currentExecutor.start();
}

// ============================================================
// Connection Events — Refresh NODE_OUTPUT Selects
// ============================================================

editor.on('connectionCreated', (connection) => {
    // connection = { output_id, input_id, output_class, input_class }
    updateNodeDataFromContent(connection.input_id); // Save existing input values before re-rendering

    // Re-render params for the target node (to refresh NODE_OUTPUT selects)
    const node = editor.getNodeFromId(connection.input_id);
    if (node && node.data && node.data.nodeKey) {
        const meta = nodeMetaList.find(m => m.nodeKey === node.data.nodeKey);
        if (meta) {
            renderNodeParams(connection.input_id, node.data, meta);
            attachNodeEventListeners(connection.input_id, node.data, meta);
        }
    }
});

editor.on('connectionRemoved', (connection) => {
    // connection = { output_id, input_id, output_class, input_class }
    updateNodeDataFromContent(connection.input_id); // Save existing input values before re-rendering

    // Re-render params for the target node
    const node = editor.getNodeFromId(connection.input_id);
    if (node && node.data && node.data.nodeKey) {
        const meta = nodeMetaList.find(m => m.nodeKey === node.data.nodeKey);
        if (meta) {
            renderNodeParams(connection.input_id, node.data, meta);
            attachNodeEventListeners(connection.input_id, node.data, meta);
        }
    }
});

// ============================================================
// Logging
// ============================================================

function log(msg) {
    const logDiv = document.getElementById('log');
    if (!logDiv) return;
    const p = document.createElement('p');
    p.textContent = `[${new Date().toLocaleTimeString()}] ${msg}`;
    logDiv.prepend(p);
}
