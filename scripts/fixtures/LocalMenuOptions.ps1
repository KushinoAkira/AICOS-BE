# Shared fixture definitions. Existing labels/prices are retained during expansion.
function Get-OptionGroups([string]$Preset) {
    if ($Preset -eq 'none') { return }
    if ($Preset -eq 'food') {
        return @{ name='Chế biến'; kind='Other'; selectionType='Single'; isRequired=$true; sortOrder=0
            options=@(@{name='Làm nóng';priceDelta=0;isDefault=$true},@{name='Giữ nguyên';priceDelta=0;isDefault=$false}) }
    }
    if ($Preset -ne 'combo') {
        @{name='Cỡ ly';kind='Size';selectionType='Single';isRequired=$true;sortOrder=0
          options=@(@{name='M';priceDelta=0;isDefault=$true},@{name='L';priceDelta=8000;isDefault=$false})}
    }
    @{name='Đường';kind='Sugar';selectionType='Single';isRequired=$true;sortOrder=1
      options=@(@{name='100%';priceDelta=0;isDefault=$true},@{name='70%';priceDelta=0;isDefault=$false},@{name='50%';priceDelta=0;isDefault=$false},@{name='30%';priceDelta=0;isDefault=$false},@{name='Không đường';priceDelta=0;isDefault=$false})}
    @{name='Đá';kind='Ice';selectionType='Single';isRequired=$true;sortOrder=2
      options=@(@{name='Bình thường';priceDelta=0;isDefault=$true},@{name='Ít đá';priceDelta=0;isDefault=$false},@{name='Không đá';priceDelta=0;isDefault=$false},@{name='Đá để riêng';priceDelta=0;isDefault=$false})}
    $choices = switch ($Preset) {
      'tea' { @(@('Trân châu',7000),@('Trân châu trắng',7000),@('Thạch trái cây',7000),@('Kem sữa',10000),@('Pudding trứng',8000),@('Thạch cà phê',7000),@('Nha đam',7000),@('Hạt sen',10000)) }
      'coffee' { @(@('Kem sữa',10000),@('Thạch cà phê',7000),@('Thêm sữa đặc',5000),@('Thêm sữa tươi',5000),@('Thêm một shot cà phê',10000)) }
      'fruit' { @(@('Nha đam',7000),@('Thạch dừa',7000),@('Thạch trái cây',7000)) }
      default { @(@('Kem sữa',10000),@('Thạch cà phê',7000),@('Trân châu',7000),@('Pudding trứng',8000),@('Thêm sữa tươi',5000)) }
    }
    @{name='Topping';kind='Topping';selectionType='Multiple';isRequired=$false;maxSelections=$null;sortOrder=3
      options=@(foreach($choice in $choices){@{name=$choice[0];priceDelta=$choice[1];isDefault=$false}})}
}