import { ReactNode, useEffect, useRef } from 'react';

interface CheckboxProps {
  id: string;
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  description?: string;
}

/**
 * A styled checkbox component with optional description
 */
export const Checkbox = ({ id, label, checked, onChange, description }: CheckboxProps) => {
  // We need to use a ref to get access to the rendered element
  const containerRef = useRef<HTMLDivElement>(null);

  // Function to set up event listeners on the checkbox once it's rendered
  useEffect(() => {
    if (containerRef.current) {
      // Find the checkbox element inside the container
      const checkbox = containerRef.current.querySelector(`#${id}`) as HTMLInputElement;
      if (checkbox) {
        // Set the checked state
        checkbox.checked = checked;

        // Add event listener to handle changes
        const handleChange = () => {
          onChange(checkbox.checked);
        };
        checkbox.addEventListener('change', handleChange);

        return () => {
          checkbox.removeEventListener('change', handleChange);
        };
      }
    }
  }, [id, checked, onChange]);

  return (
    <div ref={containerRef} className="checkboxContainer checkboxContainer-withDescription">
      <div dangerouslySetInnerHTML={{
        __html: `
          <label class="emby-checkbox-label">
              <input id="${id}" type="checkbox" is="emby-checkbox" ${checked ? 'checked' : ''} class="emby-checkbox" />
              <span>${label}</span>
          </label>
        `
      }} />

      {description && (
        <div className="fieldDescription" dangerouslySetInnerHTML={{ __html: description }} />
      )}
    </div>
  );
};

interface InputFieldProps {
  id: string;
  label: string;
  value: string | number;
  onChange: (value: string) => void;
  type?: 'text' | 'number' | 'hidden';
  min?: number;
  max?: number;
  step?: number;
  description?: string;
}

/**
 * A styled input field component with optional description
 */
export const InputField = ({
  id,
  label,
  value,
  onChange,
  type = 'text',
  min,
  max,
  step,
  description
}: InputFieldProps) => {
  return (
    <div className="inputContainer">
      <label className="inputLabel inputLabelUnfocused" htmlFor={id}>
        {label}
      </label>

      <input
        id={id}
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="emby-input"
        min={min}
        max={max}
        step={step}
      />

      {description && (
        <div className="fieldDescription">
          {description}
        </div>
      )}
    </div>
  );
};

interface SelectProps {
  id: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  options: Array<{ value: string; label: string }>;
  description?: string;
}

/**
 * A styled select component with optional description
 */
export const Select = ({
  id,
  label,
  value,
  onChange,
  options,
  description
}: SelectProps) => {
  return (
    <div className="selectContainer">
      <label className="selectLabel" htmlFor={id}>
        {label}
      </label>

      <select
        id={id}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="emby-select-withcolor emby-select"
      >
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>

      {description && (
        <div className="fieldDescription">
          {description}
        </div>
      )}
    </div>
  );
};

interface ButtonProps {
  children: React.ReactNode;
  type?: 'button' | 'submit' | 'reset';
  fullWidth?: boolean;
  onClick?: () => void;
  disabled?: boolean;
  style?: React.CSSProperties;
  variant?: 'danger' | 'primary' | 'secondary';
}

/**
 * A styled button component
 */
export const Button: React.FC<ButtonProps> = ({
  children,
  type = 'button',
  fullWidth = false,
  onClick,
  disabled = false, // Add default value
}) => {
  return (
    <button
      type={type}
      className={`emby-button button-submit block${fullWidth ? ' button-fullwidth' : ''}`}
      onClick={onClick}
      disabled={disabled} // Pass the disabled prop to the button
    >
      {children}
    </button>
  );
};

interface SectionProps {
  title: string;
  children: ReactNode;
  subtitle?: string;
}

/**
 * A styled section component with title
 */
export const Section = ({ title, subtitle, children }: SectionProps) => {
  return (
    <fieldset className="verticalSection verticalSection-extrabottompadding">
      <legend>{title}</legend>
      {subtitle && <p className="fieldDescription">{subtitle}</p>}
      {children}
    </fieldset>
  );
};

interface CollapsibleSectionProps {
  title: string;
  children: ReactNode;
  defaultOpen?: boolean;
}

/**
 * A styled collapsible section component with title
 */
export const CollapsibleSection = ({
  title,
  children,
  defaultOpen = false
}: CollapsibleSectionProps) => {
  return (
    <details open={defaultOpen}>
      <summary>{title}</summary>
      <div className="verticalSection-extrabottompadding">
        {children}
      </div>
    </details>
  );
};

interface CheckboxListProps {
  items: string[];
  selectedItems: string[];
  onChange: (selectedItems: string[]) => void;
  title: string;
}

/**
 * A styled checkbox list component
 */
export const CheckboxList = ({
  items,
  selectedItems,
  onChange,
  title
}: CheckboxListProps) => {
  const handleCheckboxChange = (item: string, checked: boolean) => {
    if (checked) {
      onChange([...selectedItems, item]);
    } else {
      onChange(selectedItems.filter(i => i !== item));
    }
  };

  return (
    <div className="checkboxList">
      <h3 className="checkboxListLabel">{title}</h3>
      <div className="checkboxList paperList" style={{ padding: '0.5em 1em' }}>
        {items.map(item => (
          <div key={item} className="checkboxContainer">
            <label className="emby-checkbox-label">
              <input
                type="checkbox"
                checked={selectedItems.includes(item)}
                onChange={(e) => handleCheckboxChange(item, e.target.checked)}
                className="emby-checkbox"
              />
              <span>{item}</span>
            </label>
          </div>
        ))}
      </div>
    </div>
  );
};
