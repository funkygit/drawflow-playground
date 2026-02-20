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
                    inputElement = this.createInputField(param);
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
}
