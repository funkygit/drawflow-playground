/**
 * Shared workflow utility functions used by both the Editor (Index) and List pages.
 * Depends on: DynamicFormRenderer (dynamic-form-renderer.js)
 */

// ============================================================
// Node Content HTML Builder
// ============================================================

/**
 * Builds the full HTML content for a Drawflow node.
 * @param {Object} meta - Node metadata from nodeMetaList
 * @param {Object} data - Node data (nodeKey, selectedVariant, parameters, etc.)
 * @param {string|number} nodeId - The Drawflow node ID
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

    // Update data button
    html += `<button class="node-update-btn" type="button">Update</button>`;

    html += `</div>`;
    return html;
}

// ============================================================
// Render Node Params
// ============================================================

/**
 * Renders the parameter table inside a node's content using DynamicFormRenderer.
 * Also populates existing values from data.parameters.
 * @param {string|number} nodeId
 * @param {Object} data - Node data with parameters
 * @param {Object} meta - Node metadata
 * @param {Function} findPrevNodesFn - Function(nodeId, requiredType) => compatible nodes array
 */
function renderNodeParams(nodeId, data, meta, findPrevNodesFn) {
    const nodeEl = document.getElementById(`node-${nodeId}`);
    if (!nodeEl) return;

    const paramsContainer = nodeEl.querySelector('.node-params-container');
    if (!paramsContainer) return;

    const findFn = findPrevNodesFn || (() => []);

    const renderer = new DynamicFormRenderer(meta, (requiredType) => {
        return findFn(nodeId, requiredType);
    });

    renderer.renderAllVariantsTable(paramsContainer, data.selectedVariant, (requiredType) => {
        return findFn(nodeId, requiredType);
    });

    // Populate existing values if any
    if (data.parameters) {
        Object.keys(data.parameters).forEach(key => {
            const el = paramsContainer.querySelector(`[name="${key}"]`);
            if (el) el.value = data.parameters[key];
        });
    }
}

// ============================================================
// Find Connected Previous Nodes (BFS)
// ============================================================

/**
 * Finds previous nodes connected to currentNodeId that output the requiredType.
 * Traverses all ancestors (BFS), not just immediate parents.
 * @param {Object} editor - Drawflow editor instance
 * @param {Array} nodeMetaList - Node metadata list
 * @param {string|number} currentNodeId
 * @param {string} requiredType
 * @returns {Array<{nodeId, outputName, label}>}
 */
function findConnectedPreviousNodes(editor, nodeMetaList, currentNodeId, requiredType) {
    const data = editor.export();
    if (!data.drawflow.Home || !data.drawflow.Home.data) return [];

    const nodes = data.drawflow.Home.data;
    const currentNode = nodes[currentNodeId];
    if (!currentNode || !currentNode.inputs) return [];

    const compatible = [];
    const seenIds = new Set();

    // BFS to collect ALL ancestor node IDs (not just immediate parents)
    const queue = [currentNodeId];
    const visited = new Set([String(currentNodeId)]);

    while (queue.length > 0) {
        const nodeId = queue.shift();
        const node = nodes[nodeId];
        if (!node || !node.inputs) continue;

        for (const inputKey in node.inputs) {
            const connections = node.inputs[inputKey].connections;
            if (connections) {
                connections.forEach(conn => {
                    const id = String(conn.node);
                    if (!visited.has(id)) {
                        visited.add(id);
                        seenIds.add(conn.node);
                        queue.push(conn.node);
                    }
                });
            }
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
// Output Port Labels
// ============================================================

/**
 * Adds text labels to output ports for control nodes (e.g., True/False, CurrentItem/Exit).
 */
function labelOutputPorts(nodeId, meta) {
    if (!meta || meta.nodeTypeKey !== 'control') return;

    const defaultVariant = meta.variants && meta.variants.length > 0 ? meta.variants[0] : null;
    if (!defaultVariant || !defaultVariant.outputs || defaultVariant.outputs.length <= 1) return;

    const nodeEl = document.getElementById(`node-${nodeId}`);
    if (!nodeEl) return;

    defaultVariant.outputs.forEach((output, index) => {
        const portEl = nodeEl.querySelector(`.output_${index + 1}`);
        if (portEl && !portEl.querySelector('.port-label')) {
            const label = document.createElement('span');
            label.className = 'port-label';
            label.textContent = output.name;
            portEl.appendChild(label);
        }
    });
}

// ============================================================
// Update Node Data from DOM
// ============================================================

/**
 * Scrapes current input/select values from a node's DOM and updates the
 * Drawflow node's data.parameters object.
 * @param {Object} editor - Drawflow editor instance
 * @param {string|number} nodeId
 */
function updateNodeDataFromContent(editor, nodeId) {
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

    // Also update selectedVariant from the variant select if present
    const variantSelect = nodeEl.querySelector('.node-variant-select');
    if (variantSelect) {
        node.data.selectedVariant = variantSelect.value;
    }
}

// ============================================================
// Clean Data Extraction (Save)
// ============================================================

/**
 * Auto-refreshes all node params from the DOM, exports the Drawflow data,
 * and strips the `html` field from every node to produce clean, execution-only JSON.
 * @param {Object} editor - Drawflow editor instance
 * @returns {Object} Clean Drawflow JSON (same structure, minus html fields)
 */
function extractCleanData(editor) {
    // 1. Auto-refresh all node params from DOM
    const exportData = editor.export();
    if (exportData.drawflow.Home && exportData.drawflow.Home.data) {
        const nodes = exportData.drawflow.Home.data;
        for (const nodeId in nodes) {
            updateNodeDataFromContent(editor, nodeId);
        }
    }

    // 2. Re-export after refresh
    const cleanData = editor.export();

    // 3. Strip html from each node
    if (cleanData.drawflow.Home && cleanData.drawflow.Home.data) {
        const nodes = cleanData.drawflow.Home.data;
        for (const nodeId in nodes) {
            delete nodes[nodeId].html;
        }
    }

    return cleanData;
}

// ============================================================
// Workflow Rehydration (Load)
// ============================================================

/**
 * Takes clean workflow data (no html), rebuilds the HTML for each node from metadata,
 * imports it into the Drawflow editor, then renders params and attaches event listeners.
 *
 * @param {Object} editor - Drawflow editor instance
 * @param {Array} nodeMetaList - Node metadata list
 * @param {Object} cleanData - Clean Drawflow JSON (no html fields)
 * @param {Object} options - Optional config
 * @param {Function} options.onNodeReady - Callback(nodeId, data, meta) called after each node is rendered
 * @param {Function} options.findPrevNodesFn - Function(nodeId, requiredType) for NODE_OUTPUT selects
 */
function rehydrateWorkflow(editor, nodeMetaList, cleanData, options = {}) {
    if (!cleanData || !cleanData.drawflow || !cleanData.drawflow.Home || !cleanData.drawflow.Home.data) {
        console.warn('rehydrateWorkflow: Invalid or empty data');
        return;
    }

    const nodes = cleanData.drawflow.Home.data;

    // 1. Rebuild html for each node from metadata
    for (const nodeId in nodes) {
        const node = nodes[nodeId];
        if (node.data && node.data.nodeKey) {
            const meta = nodeMetaList.find(m => m.nodeKey === node.data.nodeKey);
            if (meta) {
                node.html = buildNodeContentHtml(meta, node.data, nodeId);
            } else {
                // Fallback for unknown nodes
                node.html = `<div>${node.data.displayName || node.name || 'Unknown'}</div>`;
            }
        } else {
            // Legacy nodes with no nodeKey
            node.html = node.html || `<div>${(node.name || 'node').toUpperCase()}</div>`;
        }
    }

    // 2. Clear editor and import
    editor.clear();
    editor.import(cleanData);

    // 3. Post-import: render params, labels, and attach listeners for each node
    const findFn = options.findPrevNodesFn || ((nid, type) => findConnectedPreviousNodes(editor, nodeMetaList, nid, type));

    for (const nodeId in nodes) {
        const node = nodes[nodeId];
        if (node.data && node.data.nodeKey) {
            const meta = nodeMetaList.find(m => m.nodeKey === node.data.nodeKey);
            if (meta) {
                // Patch the node ID badge
                const nodeEl = document.getElementById(`node-${nodeId}`);
                if (nodeEl) {
                    const idBadge = nodeEl.querySelector('.node-id-value');
                    if (idBadge) idBadge.textContent = nodeId;
                }

                // Render params with existing values
                renderNodeParams(nodeId, node.data, meta, findFn);

                // Add output port labels
                labelOutputPorts(nodeId, meta);

                // Callback for page-specific setup (e.g., attach event listeners)
                if (options.onNodeReady) {
                    options.onNodeReady(nodeId, node.data, meta);
                }
            }
        }
    }
}
