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
let currentFormRenderer = null;

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

connection.on("NodeStatusChanged", (executionId, nodeId, status) => {
    // Filter for current execution
    if (currentExecutor && currentExecutor.executionId === executionId) {
        log(`Node ${nodeId}: ${status}`);
        updateNodeVisuals(nodeId, status);
        
        if (status === "Completed") {
            currentExecutor.handleNodeCompletion(nodeId);
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

function addNodeToDrawflow(type, posx, posy, nodeKey = null) {
    if(editor.editor_mode === 'fixed') return false;
    posx = posx * ( editor.precanvas.clientWidth / (editor.precanvas.clientWidth * editor.zoom)) - (editor.precanvas.getBoundingClientRect().x * ( editor.precanvas.clientWidth / (editor.precanvas.clientWidth * editor.zoom)));
    posy = posy * ( editor.precanvas.clientHeight / (editor.precanvas.clientHeight * editor.zoom)) - (editor.precanvas.getBoundingClientRect().y * ( editor.precanvas.clientHeight / (editor.precanvas.clientHeight * editor.zoom)));

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
            html = `<div><strong>${meta.displayName}</strong><br/><small>${meta.nodeType}</small></div>`;
            
            // Logic for IO based on type or defaults
            if (meta.nodeTypeKey === 'input') inputs = 0;
            if (meta.nodeTypeKey === 'output') outputs = 0;
        }
    } else {
        if(type === 'start') { inputs = 0; outputs = 1; }
        if(type === 'end') { inputs = 1; outputs = 0; }
        if(type === 'loop') { inputs = 1; outputs = 1; }
        if(type === 'conditional') { inputs = 1; outputs = 2; }
        if(type === 'iterator') { inputs = 1; outputs = 2; }
    }

    editor.addNode(type, inputs, outputs, posx, posy, type, data, html);
}

// API Calls
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

// Node Selection & Config
editor.on('nodeSelected', (id) => {
    const node = editor.getNodeFromId(id);
    if (node.data && node.data.nodeKey) {
        showNodeConfig(id, node.data);
    } else {
        hideNodeConfig();
    }
});

editor.on('nodeUnselected', () => hideNodeConfig());

function showNodeConfig(nodeId, data) {
    const panel = document.getElementById('node-config-panel');
    const meta = nodeMetaList.find(m => m.nodeKey === data.nodeKey);
    
    if (!meta) return;

    panel.style.display = 'block';
    document.getElementById('config-node-name').innerText = meta.displayName;
    document.getElementById('config-node-id').innerText = `(#${nodeId})`;

    // Setup Variant Selector
    const selector = document.getElementById('variant-selector');
    selector.innerHTML = '';
    meta.variants.forEach(v => {
        const opt = document.createElement('option');
        opt.value = v.value;
        opt.innerText = v.label;
        if (v.value === data.selectedVariant) opt.selected = true;
        selector.appendChild(opt);
    });

    selector.onchange = (e) => {
        data.selectedVariant = e.target.value;
        renderParams(nodeId, data, meta);
    };

    renderParams(nodeId, data, meta);
}

function renderParams(nodeId, data, meta) {
    const container = document.getElementById('dynamic-form-container');
    
    currentFormRenderer = new DynamicFormRenderer(meta, (requiredType) => {
        // Find previous nodes that output matching type
        return findCompatiblePreviousNodes(nodeId, requiredType);
    });

    currentFormRenderer.renderVariantForm(data.selectedVariant, container);

    // Populate existing values if any
    if (data.parameters) {
        Object.keys(data.parameters).forEach(key => {
            const el = container.querySelector(`[name="${key}"]`);
            if (el) el.value = data.parameters[key];
        });
    }
}

function findCompatiblePreviousNodes(currentNodeId, requiredType) {
    const nodes = editor.export().drawflow.Home.data;
    const compatible = [];

    Object.keys(nodes).forEach(id => {
        if (id == currentNodeId) return; // Skip self

        const node = nodes[id];
        if (node.data && node.data.nodeKey) {
            const meta = nodeMetaList.find(m => m.nodeKey === node.data.nodeKey);
            const variant = meta?.variants.find(v => v.value === node.data.selectedVariant);
            
            if (variant && variant.outputs) {
                variant.outputs.forEach(output => {
                    if (output.dataType === requiredType) {
                        compatible.push({
                            nodeId: id,
                            outputName: output.name,
                            label: `${node.data.displayName} (${output.name})`
                        });
                    }
                });
            }
        }
    });

    return compatible;
}

function updateNodeData() {
    const nodeId = document.getElementById('config-node-id').innerText.replace(/[()#]/g, '');
    const data = editor.getNodeFromId(nodeId).data;
    const container = document.getElementById('dynamic-form-container');

    const inputs = container.querySelectorAll('input, select');
    data.parameters = {};
    inputs.forEach(input => {
        if (input.name) {
            data.parameters[input.name] = input.value;
        }
    });

    log(`Updated Node ${nodeId} data`);
}

function hideNodeConfig() {
    document.getElementById('node-config-panel').style.display = 'none';
}

function log(msg) {
    const logDiv = document.getElementById('log');
    if (!logDiv) return;
    const p = document.createElement('p');
    p.textContent = `[${new Date().toLocaleTimeString()}] ${msg}`;
    logDiv.prepend(p);
}
