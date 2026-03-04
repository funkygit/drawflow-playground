/**
 * Utility for rendering dynamic forms for Workflow Nodes based on metadata.
 */
class DynamicFormRenderer {
    /**
     * @param {Object} nodeMeta - The metadata for the current node module.
     * @param {Function} findPreviousCompatibleNodes - Function (type) => Array<{nodeId, outputName, label}>
     */
    constructor(nodeMeta, findPreviousCompatibleNodes) {
        this.nodeMeta = nodeMeta;
        this.findPreviousCompatibleNodes = findPreviousCompatibleNodes;
    }

    /**
     * Renders the parameter form for a selected variant.
     * @param {string} variantValue - The value of the selected variant (e.g., "AWS").
     * @param {HTMLElement} container - The container where the form should be rendered.
     */
    renderVariantForm(variantValue, container) {
        container.innerHTML = ''; // Clear container

        const variant = this.nodeMeta.variants.find(v => v.value === variantValue);
        if (!variant) return;

        variant.parameters.forEach(param => {
            const formGroup = document.createElement('div');
            formGroup.className = 'form-group mb-3';

            const label = document.createElement('label');
            label.innerText = param.displayName || param.name;
            formGroup.appendChild(label);

            let inputElement;

            switch (param.source) {
                case 'USER_INPUT':
                    if (param.allowedValues && param.allowedValues.length > 0) {
                        inputElement = this.createSelectField(param);
                    } else {
                        inputElement = this.createInputField(param);
                    }
                    break;
                case 'CONSTANT':
                    inputElement = this.createReadOnlyField(param);
                    break;
                case 'NODE_OUTPUT':
                    inputElement = this.createOutputSelect(param);
                    break;
                default:
                    inputElement = document.createElement('span');
                    inputElement.innerText = 'Unsupported Source';
            }

            formGroup.appendChild(inputElement);
            container.appendChild(formGroup);
        });
    }

    /**
     * Renders a <table> with parameter rows from ALL variants.
     * Each row is tagged with data-variant. Rows not matching selectedVariant
     * are hidden with the 'variant-hidden' CSS class.
     * @param {HTMLElement} container - Container element to render into
     * @param {string} selectedVariant - Currently selected variant value
     * @param {Function} findConnectedNodes - Function(dataType) => Array<{nodeId, outputName, label}>
     */
    renderAllVariantsTable(container, selectedVariant, findConnectedNodes) {
        container.innerHTML = '';

        const table = document.createElement('table');
        table.className = 'node-params-table';
        const tbody = document.createElement('tbody');

        // Collect all unique parameters across all variants, keyed by param name
        // Track which variant(s) each param belongs to
        const paramVariantMap = new Map(); // paramName => Set of variant values

        this.nodeMeta.variants.forEach(variant => {
            if (!variant.parameters) return;
            variant.parameters.forEach(param => {
                if (!paramVariantMap.has(param.name)) {
                    paramVariantMap.set(param.name, { param, variants: new Set() });
                }
                paramVariantMap.get(param.name).variants.add(variant.value);
            });
        });

        // Render rows
        paramVariantMap.forEach(({ param, variants }, paramName) => {
            const tr = document.createElement('tr');
            tr.className = 'node-param-row';
            // Store all variants this param belongs to
            tr.dataset.variants = JSON.stringify([...variants]);
            tr.dataset.paramName = paramName;

            // Check if this row should be visible
            if (!variants.has(selectedVariant)) {
                tr.classList.add('variant-hidden');
            }

            // Store VisibleWhen condition if present
            if (param.visibleWhen) {
                tr.dataset.visibleWhenField = param.visibleWhen.field;
                tr.dataset.visibleWhenValue = param.visibleWhen.value;
            }

            // Label cell
            const tdLabel = document.createElement('td');
            tdLabel.textContent = param.displayName || param.name;
            tr.appendChild(tdLabel);

            // Input cell
            const tdInput = document.createElement('td');
            let inputElement;

            switch (param.source) {
                case 'USER_INPUT':
                    if (param.allowedValues && param.allowedValues.length > 0) {
                        inputElement = this.createSelectField(param);
                    } else {
                        inputElement = this.createInputField(param);
                    }
                    break;
                case 'CONSTANT':
                    inputElement = this.createReadOnlyField(param);
                    break;
                case 'NODE_OUTPUT':
                    inputElement = this.createOutputSelectInline(param, findConnectedNodes);
                    break;
                default:
                    inputElement = document.createElement('span');
                    inputElement.textContent = '—';
            }

            tdInput.appendChild(inputElement);
            tr.appendChild(tdInput);
            tbody.appendChild(tr);
        });

        table.appendChild(tbody);
        container.appendChild(table);

        // Apply conditional visibility (VisibleWhen)
        this._applyConditionalVisibility(container);
    }

    /**
     * Shows/hides parameter rows based on the selected variant.
     * @param {HTMLElement} container - The container with the params table
     * @param {string} selectedVariant - The newly selected variant value
     */
    toggleVariantVisibility(container, selectedVariant) {
        const rows = container.querySelectorAll('.node-param-row');
        rows.forEach(row => {
            const variants = JSON.parse(row.dataset.variants || '[]');
            if (variants.includes(selectedVariant)) {
                row.classList.remove('variant-hidden');
            } else {
                row.classList.add('variant-hidden');
            }
        });

        // Re-apply conditional visibility after variant change
        this._applyConditionalVisibility(container);
    }

    /**
     * Applies conditional visibility based on VisibleWhen rules.
     * Rows with data-visible-when-field are shown/hidden based on the
     * current value of the controlling field.
     * Also attaches change listeners for dynamic toggling.
     */
    _applyConditionalVisibility(container) {
        const conditionalRows = container.querySelectorAll('[data-visible-when-field]');
        if (conditionalRows.length === 0) return;

        // Collect controlling field names
        const controllingFields = new Set();
        conditionalRows.forEach(row => controllingFields.add(row.dataset.visibleWhenField));

        // For each controlling field, find its input element and apply visibility
        controllingFields.forEach(fieldName => {
            const controllingInput = container.querySelector(`[name="${fieldName}"]`);
            if (!controllingInput) return;

            const applyVisibility = () => {
                const currentValue = controllingInput.value;
                conditionalRows.forEach(row => {
                    if (row.dataset.visibleWhenField !== fieldName) return;
                    if (row.dataset.visibleWhenValue === currentValue) {
                        row.classList.remove('condition-hidden');
                    } else {
                        row.classList.add('condition-hidden');
                    }
                });
            };

            // Apply immediately
            applyVisibility();

            // Attach change listener (remove previous to avoid duplicates)
            controllingInput.removeEventListener('change', controllingInput._visibilityHandler);
            controllingInput._visibilityHandler = applyVisibility;
            controllingInput.addEventListener('change', applyVisibility);
        });
    }

    createInputField(param) {
        const input = document.createElement('input');
        input.className = 'form-control';
        input.name = param.name;

        // Map C# types to HTML input types
        if (param.dataType === 'System.Int32' || param.dataType === 'System.Double') {
            input.type = 'number';
        } else if (param.dataType === 'System.Boolean') {
            input.type = 'checkbox';
            input.className = 'form-check-input';
        } else {
            input.type = 'text';
        }

        // Add additional metadata as data-props
        input.dataset.name = param.name;
        input.dataset.dataType = param.dataType;
        input.dataset.source = param.source;

        return input;
    }

    createReadOnlyField(param) {
        const input = document.createElement('input');
        input.className = 'form-control-plaintext';
        input.readOnly = true;
        input.value = param.value || '';
        input.dataset.source = 'CONSTANT';
        return input;
    }

    createOutputSelect(param) {
        const wrapper = document.createElement('div');
        wrapper.className = 'd-flex align-items-center gap-2';

        const secondaryLabel = document.createElement('span');
        secondaryLabel.className = 'text-muted small';
        secondaryLabel.innerText = 'Output from';
        wrapper.appendChild(secondaryLabel);

        const select = document.createElement('select');
        select.className = 'form-select form-select-sm';
        select.name = param.name;
        select.dataset.source = 'NODE_OUTPUT';
        select.dataset.dataType = param.dataType;

        // Populate options using the provided discovery function
        const compatibleNodes = this.findPreviousCompatibleNodes(param.dataType);

        const defaultOption = document.createElement('option');
        defaultOption.value = '';
        defaultOption.innerText = '-- Select Source --';
        select.appendChild(defaultOption);

        compatibleNodes.forEach(node => {
            const option = document.createElement('option');
            option.value = `${node.nodeId}.${node.outputName}`;
            option.innerText = node.label;
            select.appendChild(option);
        });

        wrapper.appendChild(select);
        return wrapper;
    }

    /**
     * Compact output select for in-node table rendering.
     * Takes a findConnectedNodes callback directly.
     */
    createOutputSelectInline(param, findConnectedNodes) {
        const select = document.createElement('select');
        select.name = param.name;
        select.dataset.source = 'NODE_OUTPUT';
        select.dataset.dataType = param.dataType;

        const defaultOption = document.createElement('option');
        defaultOption.value = '';
        defaultOption.textContent = '-- Source --';
        select.appendChild(defaultOption);

        const compatibleNodes = findConnectedNodes(param.dataType);
        compatibleNodes.forEach(node => {
            const option = document.createElement('option');
            option.value = `${node.nodeId}.${node.outputName}`;
            option.textContent = node.label;
            select.appendChild(option);
        });

        return select;
    }

    createSelectField(param) {
        const select = document.createElement('select');
        select.className = 'form-select';
        select.name = param.name;
        select.dataset.name = param.name;
        select.dataset.dataType = param.dataType;
        select.dataset.source = param.source;

        param.allowedValues.forEach(val => {
            const option = document.createElement('option');
            option.value = val;
            option.innerText = val;
            if (param.value && param.value === val) {
                option.selected = true;
            }
            select.appendChild(option);
        });

        return select;
    }
}
